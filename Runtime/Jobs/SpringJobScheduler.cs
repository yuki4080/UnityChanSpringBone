using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using FUtility;

namespace Unity.Animations.SpringBones.Jobs {
	/// <summary>
	/// SpringBoneのNativeContainer管理とJob発行
	/// </summary>
	[DefaultExecutionOrder(-200)] // NOTE: LateUpdateの頭で呼んであげることで非同期の並列処理をする算段
	public class SpringJobScheduler : MonoBehaviour {
		private enum TRANSFORM_JOB {
			PARENT_BONE,
			PIVOT_BONE,
			COLLIDER,
			LENGTH_LIMIT,

			MAX,
		}

		[SerializeField]
		private bool asynchronize = false;    // 非同期
		[SerializeField]
		private int maxWorkerThreadCount = 0; // 最大使用WorkerThread数（0で無制限）
		[SerializeField]
		private int registerCapacity = 32;    // 登録最大数
		[SerializeField]
		private int boneCapacity = 512;       // ボーン最大数
		[SerializeField]
		private int colliderCapacity = 256;   // コライダー最大数
		[SerializeField]
		private int registedColliderCapacity = 2048;    // コリジョンインデックス最大数
		[SerializeField]
		private int registeredLengthLimitCapacity = 256; // 長さ制限最大数

		private static SpringJobScheduler instance = null;

		// ジョブ渡しバッファ
		internal NativeContainerPool<SpringBoneProperties> properties;
		internal NativeArray<SpringBoneComponents> components;
		internal NativeArray<Matrix4x4> parentComponents;
		internal NativeArray<Matrix4x4> pivotComponents;
		internal NativeContainerPool<SpringColliderProperties> colProperties;
		internal NativeArray<SpringColliderComponents> colComponents;
		internal NativeContainerPool<int> colNumbers;
		internal NativeContainerPool<LengthLimitProperties> lengthProperties;
		internal NativeContainerPool<Vector3> lengthLimitTargets;

		internal TransformAccessArray boneTransforms;
		internal TransformAccessArray boneParentTransforms;
		internal TransformAccessArray bonePivotTransforms;
		internal TransformAccessArray colliderTransforms;
		internal TransformAccessArray lengthLimitTransforms;

		private TaskSystem<SpringJobManager> managerTasks;
		private NativeArray<SpringJobChild> managers;
		private NativeArray<JobHandle> preHandle;
		private SpringBoneApplyJob applyJob;
		private SpringParentJob parentJob;
		private SpringPivotJob pivotJob;
		private SpringColliderJob colliderJob;
		private SpringLengthTargetJob lengthTargetJob;
		private SpringJob springJob;
		private JobHandle handle;
		private bool scheduled = false;


		/// <summary>
		/// 同期／非同期のスイッチ
		/// </summary>
		public bool enabledAsync { get { return this.asynchronize; } set { this.asynchronize = value; } }


		/// <summary>
		/// 生成
		/// </summary>
		void Awake() {
			// NOTE: 事前にScene内に用意していた場合の対応
			this.Initialize();
		}

		/// <summary>
		/// 初期化
		/// </summary>
		private void Initialize() {
			if (instance != null)
				return;
			instance = this;
			Object.DontDestroyOnLoad(this.gameObject);

			// NOTE: プロジェクト全体に影響するので機能単位で設定変更すべきでない
			//// Render Threadが有効な場合コンテキストスイッチを考慮してWorkerThread分を削減した方がよいのでは？
			//// Editorで設定すると再設定しないと戻らないので割と面倒 
			//int workerCount = JobsUtility.JobWorkerMaximumCount;
			//if (workerCount > 1) {
			//	if (SystemInfo.renderingThreadingMode == UnityEngine.Rendering.RenderingThreadingMode.MultiThreaded ||
			//		SystemInfo.renderingThreadingMode == UnityEngine.Rendering.RenderingThreadingMode.NativeGraphicsJobs)
			//		workerCount -= 1;
			//}
			//JobsUtility.JobWorkerCount = workerCount;

			// 0以下の補正
			this.registerCapacity = Mathf.Max(1, this.registerCapacity);
			this.boneCapacity = Mathf.Max(1, this.boneCapacity);
			this.colliderCapacity = Mathf.Max(1, this.colliderCapacity);
			this.registedColliderCapacity = Mathf.Max(1, this.registedColliderCapacity);
			this.registeredLengthLimitCapacity = Mathf.Max(1, this.registeredLengthLimitCapacity);

			// NativeContainer作成
			this.properties = new NativeContainerPool<SpringBoneProperties>(this.boneCapacity, this.registerCapacity);
			this.components = new NativeArray<SpringBoneComponents>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.parentComponents = new NativeArray<Matrix4x4>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.pivotComponents = new NativeArray<Matrix4x4>(this.boneCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.colProperties = new NativeContainerPool<SpringColliderProperties>(this.colliderCapacity, this.registerCapacity);
			this.colComponents = new NativeArray<SpringColliderComponents>(this.colliderCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.colNumbers = new NativeContainerPool<int>(this.registedColliderCapacity, this.boneCapacity);
			this.lengthProperties = new NativeContainerPool<LengthLimitProperties>(this.registeredLengthLimitCapacity, this.boneCapacity);
			this.lengthLimitTargets = new NativeContainerPool<Vector3>(this.registeredLengthLimitCapacity, this.registerCapacity);

			this.boneTransforms = new TransformAccessArray(new Transform[this.boneCapacity], 1);
			this.boneParentTransforms = new TransformAccessArray(new Transform[this.boneCapacity]);
			this.bonePivotTransforms = new TransformAccessArray(new Transform[this.boneCapacity]);
			this.colliderTransforms = new TransformAccessArray(new Transform[this.colliderCapacity]);
			this.lengthLimitTransforms = new TransformAccessArray(new Transform[this.registeredLengthLimitCapacity]);

			this.applyJob.components = this.components;
			this.parentJob.properties = this.properties.nativeArray;
			this.parentJob.components = this.parentComponents;
			this.pivotJob.properties = this.properties.nativeArray;
			this.pivotJob.components = this.pivotComponents;
			this.colliderJob.components = this.colComponents;
			//this.lengthLimitJob.properties = this.lengthProperties.nativeArray; // NOTE: 必要なら
			this.lengthTargetJob.components = this.lengthLimitTargets.nativeArray;

			this.managerTasks = new TaskSystem<SpringJobManager>(this.registerCapacity);
			this.managers = new NativeArray<SpringJobChild>(this.registerCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			this.preHandle = new NativeArray<JobHandle>((int)TRANSFORM_JOB.MAX, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

			this.springJob.jobManagers = this.managers;

			// 稼働しているWorkerThread数より多いの禁止（挙動としての意味はないが一応警告）
			if (this.maxWorkerThreadCount > System.Environment.ProcessorCount) {
				int workerThreadCount = System.Environment.ProcessorCount;
				Debug.Log($"SpringJobScheduler clamped maxWorkerThreadCount {this.maxWorkerThreadCount} to {workerThreadCount}");
				this.maxWorkerThreadCount = workerThreadCount;
			}
		}

		void OnDestroy() {
			if (instance == null)
				return;

			// NOTE: SpringJobの完了待ちしてからNativeContainerを解放しないと当然怒られる、非同期設定の場合に必要
			this.handle.Complete();

			this.managerTasks.Detach(AllFinishJob);

			this.managerTasks.Clear();

			this.managers.Dispose();
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
			this.colProperties.Dispose();
			this.colComponents.Dispose();
			this.colNumbers.Dispose();
			this.lengthProperties.Dispose();
			this.lengthLimitTargets.Dispose();

			instance = null;
		}

		void LateUpdate() {
			if (!this.scheduled)
				return;

			if (this.asynchronize)
				this.handle.Complete();

			int managerCount = this.managerTasks.count;
			if (managerCount == 0) {
				this.scheduled = false;
				return;
			}

			if (this.asynchronize) {
				// Spring結果反映
				var applyHandle = this.applyJob.Schedule(this.boneTransforms);

				this.preHandle[(int)TRANSFORM_JOB.PIVOT_BONE] = this.pivotJob.Schedule(this.bonePivotTransforms, applyHandle);
				this.preHandle[(int)TRANSFORM_JOB.PARENT_BONE] = this.parentJob.Schedule(this.boneParentTransforms, applyHandle);
				this.preHandle[(int)TRANSFORM_JOB.COLLIDER] = this.colliderJob.Schedule(this.colliderTransforms, applyHandle);
				this.preHandle[(int)TRANSFORM_JOB.LENGTH_LIMIT] = this.lengthTargetJob.Schedule(this.lengthLimitTransforms, applyHandle);
			} else {
				// NOTE: 可能な限り並列化出来るようにする
				this.preHandle[(int)TRANSFORM_JOB.PIVOT_BONE] = this.pivotJob.Schedule(this.bonePivotTransforms);
				this.preHandle[(int)TRANSFORM_JOB.PARENT_BONE] = this.parentJob.Schedule(this.boneParentTransforms);
				this.preHandle[(int)TRANSFORM_JOB.COLLIDER] = this.colliderJob.Schedule(this.colliderTransforms);
				this.preHandle[(int)TRANSFORM_JOB.LENGTH_LIMIT] = this.lengthTargetJob.Schedule(this.lengthLimitTransforms);
			}

			var combineHandle = JobHandle.CombineDependencies(this.preHandle);
			var innerloopBatchCount = 0;
			if (this.maxWorkerThreadCount > 0 && managerCount > this.maxWorkerThreadCount) {
				innerloopBatchCount = Mathf.CeilToInt((float)managerCount / this.maxWorkerThreadCount);
			}
			this.handle = this.springJob.Schedule(managerCount, innerloopBatchCount, combineHandle);

			if (this.asynchronize)
				JobHandle.ScheduleBatchedJobs();
			else
				this.applyJob.Schedule(this.boneTransforms, this.handle).Complete();
		}

		/// <summary>
		/// 接続
		/// </summary>
		public static bool Entry(SpringJobManager register) {
			// NOTE: Scheduler未作成なら生成してあげる
			if (instance == null) {
				var scheduler = Object.FindObjectOfType<SpringJobScheduler>();
				if (scheduler == null) {
					GameObject go = new GameObject("SpringJobScheduler(Don't destroy)");
					scheduler = go.AddComponent<SpringJobScheduler>();

					Debug.Log("Create SpringJobScheduler using default parameter");
				}
				scheduler.Initialize();
			}

			if (instance.managerTasks.count > instance.registerCapacity) {
				Debug.LogError("Spring Managerの登録上限が不足しています : " + instance.registerCapacity);
				return false;
			}

			// NOTE: NativeArray参照するので非同期対応の場合に完了待ちが必要
			//       Update内で呼ばれる想定なのでLateUpdate内からEntryされるとドリフトする
			instance.handle.Complete();

			if (register.Initialize(instance)) {
				instance.scheduled = true;
				instance.managerTasks.Attach(register);
				instance.managerTasks.Order(CollectActiveJob);
				return true;
			}

			return false;
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
			return instance.managerTasks.Detach(DetachFineshedJob); ;
		}

		#region MANAGER TASK
		public static bool CollectActiveJob(SpringJobManager manager, int no) {
			instance.springJob.jobManagers[no] = manager.GetJob();
			return true;
		}
		public static int DetachFineshedJob(SpringJobManager manager) {
			if (manager.initialized)
				return 0;
			return -1; // Detachかつ中断
		}
		public static int AllFinishJob(SpringJobManager manager) {
			manager.Final(instance);
			return 1; // Detachかつ継続
		}
        #endregion
    }
}
