//#define ASYNCHRONIZE // 1F内で計算と反映を行う

using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Jobs;
using FUtility;

namespace Unity.Animations.SpringBones.Jobs {
	/// <summary>
	/// SpringBoneのNativeContainer管理とJob発行
	/// </summary>
	public class SpringJobScheduler : MonoBehaviour {
		private enum TRANSFORM_JOB {
			PARENT_BONE,
			PIVOT_BONE,
			COLLIDER,
			LENGTH_LIMIT,

			MAX,
		}

		private int registerCapacity = 32;     // 登録最大数
		private int boneCapacity = 512;        // ボーン最大数
		private int collisionCapacity = 512;   // コリジョン最大数
		private int lengthLimitCapacity = 256; // 長さ制限最大数

		private static SpringJobScheduler instance = null;

		// ジョブ渡しバッファ
		internal NativeContainerPool<SpringBoneProperties> properties;
		internal NativeArray<SpringBoneComponents> components;
		internal NativeArray<Matrix4x4> parentComponents;
		internal NativeArray<Matrix4x4> pivotComponents;
		internal NativeContainerPool<SpringColliderProperties> colProperties;
		internal NativeArray<SpringColliderComponents> colComponents;
		internal NativeContainerPool<LengthLimitProperties> lengthProperties;
		internal NativeArray<Vector3> lengthComponents;

		internal TransformAccessArray boneTransforms;
		internal TransformAccessArray boneParentTransforms;
		internal TransformAccessArray bonePivotTransforms;
		internal TransformAccessArray colliderTransforms;
		internal TransformAccessArray lengthLimitTransforms;

		private List<SpringJobManager> managers = new List<SpringJobManager>();
		private NativeArray<SpringJobChild> managerJobs;
		private NativeArray<JobHandle> preHandle;
		private SpringBoneApplyJob applyJob;
		private SpringParentJob parentJob;
		private SpringPivotJob pivotJob;
		private SpringColliderJob colliderJob;
		private SpringLengthLimitJob lengthLimitJob;
		private SpringJob springJob;
		private JobHandle handle;
		private bool scheduled = false;


		private void Initialize() {
			// NOTE: プロジェクト全体に影響するので一機能が設定すべきでない
			//// Render Threadが有効な場合コンテキストスイッチを考慮してWorkerThreadを削減
			//int workerCount = JobsUtility.JobWorkerMaximumCount;
			//if (workerCount > 1) {
			//	if (SystemInfo.renderingThreadingMode == UnityEngine.Rendering.RenderingThreadingMode.MultiThreaded ||
			//		SystemInfo.renderingThreadingMode == UnityEngine.Rendering.RenderingThreadingMode.NativeGraphicsJobs)
			//		workerCount -= 1;
			//}
			//JobsUtility.JobWorkerCount = workerCount;

			this.properties = new NativeContainerPool<SpringBoneProperties>(this.boneCapacity, this.registerCapacity);
			this.components = new NativeArray<SpringBoneComponents>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.parentComponents = new NativeArray<Matrix4x4>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.pivotComponents = new NativeArray<Matrix4x4>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.colProperties = new NativeContainerPool<SpringColliderProperties>(this.collisionCapacity, this.registerCapacity);
			this.colComponents = new NativeArray<SpringColliderComponents>(this.collisionCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.lengthProperties = new NativeContainerPool<LengthLimitProperties>(this.lengthLimitCapacity, this.registerCapacity);
			this.lengthComponents = new NativeArray<Vector3>(this.lengthLimitCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

			// NOTE: Worker Threadが２本ならParallel化を無効にしてコンテキストスイッチを考慮
			int desiredJobCount = -1;
			if (JobsUtility.JobWorkerCount < 3)
				desiredJobCount = 1;
#if UNITY_IOS || UNITY_ANDROID
			// NOTE: Mobileはbig.LITTLEなのでParallelに期待出来ない
			desiredJobCount = 1;
#endif
			this.boneTransforms = new TransformAccessArray(new Transform[this.boneCapacity], 1);
			this.boneParentTransforms = new TransformAccessArray(new Transform[this.boneCapacity], desiredJobCount);
			this.bonePivotTransforms = new TransformAccessArray(new Transform[this.boneCapacity], desiredJobCount);
			this.colliderTransforms = new TransformAccessArray(new Transform[this.collisionCapacity], desiredJobCount);
			this.lengthLimitTransforms = new TransformAccessArray(new Transform[this.lengthLimitCapacity], desiredJobCount);

			this.applyJob.components = this.components;
			this.parentJob.components = this.parentComponents;
			this.pivotJob.components = this.pivotComponents;
			this.colliderJob.components = this.colComponents;
			this.lengthLimitJob.properties = this.lengthProperties.nativeArray;
			this.lengthLimitJob.components = this.lengthComponents;

			this.managerJobs = new NativeArray<SpringJobChild>(this.registerCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.preHandle = new NativeArray<JobHandle>((int)TRANSFORM_JOB.MAX, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
		}

		void OnDestroy() {
			if (instance == null)
				return;

			// 同期しないと当然怒られる
			this.handle.Complete();

			this.managers.Clear();

			this.managerJobs.Dispose();
			this.preHandle.Dispose();

			this.boneTransforms.Dispose();
			this.boneParentTransforms.Dispose();
			this.bonePivotTransforms.Dispose();
			this.colliderTransforms.Dispose();
			this.lengthLimitTransforms.Dispose();

			this.properties.Dispose();
			this.components.Dispose();
			this.parentComponents.Dispose();
			this.pivotComponents.Dispose();
			this.colComponents.Dispose();
			this.colProperties.Dispose();
			this.lengthProperties.Dispose();
			this.lengthComponents.Dispose();

			instance = null;
		}

		void LateUpdate() {
			if (!this.scheduled)
				return;

#if ASYNCHRONIZE
			this.handle.Complete();
#endif

			int managerCount = this.managers.Count;
			if (managerCount == 0) {
				this.scheduled = false;
				return;
			}

#if ASYNCHRONIZE
			// Spring結果反映
			var applyHandle = this.applyJob.Schedule(this.boneTransforms);
			this.preHandle[(int)TRANSFORM_JOB.PIVOT_BONE] = this.pivotJob.Schedule(this.bonePivotTransforms, applyHandle);
			this.preHandle[(int)TRANSFORM_JOB.PARENT_BONE] = this.parentJob.Schedule(this.boneParentTransforms, applyHandle);
			this.preHandle[(int)TRANSFORM_JOB.COLLIDER] = this.colliderJob.Schedule(this.colliderTransforms, applyHandle);
			this.preHandle[(int)TRANSFORM_JOB.LENGTH_LIMIT] = this.lengthLimitJob.Schedule(this.lengthLimitTransforms, applyHandle);
#else
			// NOTE: 可能な限り並列化出来るようにする
			this.preHandle[(int)TRANSFORM_JOB.PIVOT_BONE]   = this.pivotJob.Schedule(this.bonePivotTransforms);
			this.preHandle[(int)TRANSFORM_JOB.PARENT_BONE]  = this.parentJob.Schedule(this.boneParentTransforms);
			this.preHandle[(int)TRANSFORM_JOB.COLLIDER]     = this.colliderJob.Schedule(this.colliderTransforms);
			this.preHandle[(int)TRANSFORM_JOB.LENGTH_LIMIT] = this.lengthLimitJob.Schedule(this.lengthLimitTransforms);
#endif
			var combineHandle = JobHandle.CombineDependencies(this.preHandle);
			this.handle = this.springJob.Schedule(managerCount, 0, combineHandle);

#if ASYNCHRONIZE
			JobHandle.ScheduleBatchedJobs();
#else
			this.applyJob.Schedule(this.boneTransforms, this.handle).Complete();
#endif
		}

		/// <summary>
		/// 接続
		/// </summary>
		public static bool Entry(SpringJobManager register) {
			// NOTE: 暗黙で準備するのは製品にはあまり良いと言えない
			if (instance == null) {
				GameObject go = new GameObject("SpringBoneJobScheduler");
				go.hideFlags |= HideFlags.HideInHierarchy;
				instance = go.AddComponent<SpringJobScheduler>();
				instance.Initialize();
			}

			if (instance.managers.Count > instance.registerCapacity) {
				Debug.LogError("Max JobHandle Count Error : " + instance.registerCapacity);
				return false;
			}

			// NativeArray参照の為に必要
			instance.handle.Complete();

			instance.scheduled = true;
			register.Initialize(instance);
			instance.managers.Add(register);

			var activeJobs = new NativeSlice<SpringJobChild>(instance.managerJobs, 0, instance.managers.Count);
			for (int i = 0; i < instance.managers.Count; ++i)
				activeJobs[i] = instance.managers[i].GetJob();

			instance.springJob.jobArray = activeJobs;

			return true;
		}

		/// <summary>
		/// 切断
		/// </summary>
		public static bool Exit(SpringJobManager scheduler) {
			if (instance == null)
				return false;

			// 同期しないと怒られる
			instance.handle.Complete();

			scheduler.Final(instance);
			return instance.managers.Remove(scheduler); ;
		}
	}
}
