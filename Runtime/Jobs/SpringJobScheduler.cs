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

		[SerializeField]
		private int registerCapacity = 32;     // 登録最大数
		[SerializeField]
		private int boneCapacity = 512;        // ボーン最大数
		[SerializeField]
		private int collisionCapacity = 512;   // コリジョン最大数
		[SerializeField]
		private int lengthLimitCapacity = 256; // 長さ制限最大数

		private static SpringJobScheduler instance = null;

		// ジョブ渡しバッファ
		internal NativeContainerPool<SpringBoneProperties> properties;
		internal NativeArray<SpringBoneComponent> components;
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
			this.components = new NativeArray<SpringBoneComponent>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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
			//this.parentJob.properties = this.properties.nativeArray;
			this.parentJob.components = this.components;
			//this.pivotJob.properties = this.properties.nativeArray;
			this.pivotJob.components = this.pivotComponents;
			this.colliderJob.components = this.colComponents;
			this.lengthLimitJob.properties = this.lengthProperties.nativeArray;
			this.lengthLimitJob.components = this.lengthComponents;

			this.managerJobs = new NativeArray<SpringJobChild>(this.registerCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.preHandle = new NativeArray<JobHandle>((int)TRANSFORM_JOB.MAX, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

			this.springJob.pivotComponents = this.pivotComponents;
			this.springJob.components = this.components;
			this.springJob.properties = this.properties.nativeArray;
			this.springJob.colProperties = this.colProperties.nativeArray;
			this.springJob.colComponents = this.colComponents;
			this.springJob.lengthProperties = this.lengthProperties.nativeArray;
			this.springJob.lengthComponents = this.lengthComponents;
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

			// NonJob for debug
			//this.ManagedJob();

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
			this.handle = this.springJob.Schedule(combineHandle);

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
			//instance.pivotJob.jobArray = activeJobs;       // AngleLimit option
			instance.colliderJob.jobArray = activeJobs;    // Collider option
			instance.lengthLimitJob.jobArray = activeJobs; // LengthLimit option

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

		/// <summary>
		/// TransformJobを使用しない場合、テスト用
		/// </summary>
		//public void ManagedJob() {
		//	Vector3 pos; Quaternion rot;
		//	var components = this.components;
		//	var properties = this.properties.nativeArray;
		//	var colliders = this.colComponents.nativeArray;
		//	var limits = this.lengthProperties.nativeArray;
		//	var pivots = this.pivotComponents;

		//	var jobChilds = this.springJob.jobArray;
		//	int children = jobChilds.Length;
		//	for (int jobIndex = 0; jobIndex < children; ++jobIndex) {
		//		var job = jobChilds[jobIndex];
		//		if (job.boneCount == 0)
		//			continue;

		//		var setting = job.settings;

		//		for (var i = job.boneIndex; i < job.boneIndex + job.boneCount; ++i) {
		//			SpringBoneComponent bone = components[i];
		//			SpringBoneProperties prop = properties[i];

		//			// Apply
		//			this.boneTransformHandles[i].localRotation = bone.localRotation;

		//			// Parent
		//			if (prop.parentIndex < 0) {
		//				bone.parentPosition = this.boneParentTransformHandles[i].position;
		//				bone.parentRotation = this.boneParentTransformHandles[i].rotation;
		//			}

		//			// Pivot
		//			if (prop.pivotIndex < 0 && setting.enableAngleLimits) {
		//				if (prop.yAngleLimits.active > 0 || prop.zAngleLimits.active > 0) {
		//					pos = this.bonePivotTransformHandles[i].position;
		//					rot = this.bonePivotTransformHandles[i].rotation;
		//					pivots[i] = Matrix4x4.TRS(pos, rot, Vector3.one);
		//				}
		//			}

		//			components[i] = bone;
		//		}

		//		// Collider
		//		if (setting.enableCollision) {
		//			for (var i = job.colIndex; i < job.colIndex + job.colCount; ++i) {
		//				pos = this.colliderTransformHandles[i].position;
		//				rot = this.colliderTransformHandles[i].rotation;

		//				var mat = Matrix4x4.TRS(pos, rot, Vector3.one);
		//				colliders[i] = new SpringColliderComponents {
		//					position = pos,
		//					rotation = rot,
		//					localToWorldMatrix = mat,
		//					worldToLocalMatrix = Matrix4x4.Inverse(mat),
		//				};
		//			}
		//		}

		//		// LengthLimit
		//		if (setting.enableLengthLimits) {
		//			for (var i = job.lengthIndex; i < job.lengthIndex + job.lengthCount; ++i) {
		//				var component = this.lengthProperties.nativeArray[i];
		//				if (component.targetIndex >= 0)
		//					continue;
		//				component.position = this.lengthLimitTransformHandles[i].position;
		//				limits[i] = component;
		//			}
		//		}
		//	}
		//}
	}
}
