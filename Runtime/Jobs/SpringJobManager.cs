using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using System.Linq;
using FUtility;

namespace Unity.Animations.SpringBones.Jobs {
	/// <summary>
	/// Job版SpringManager
	/// </summary>
	public class SpringJobManager : MonoBehaviour {
		#region MEMBER
		[Header("Optional Settings")] 
		public bool optimizeTransform = false;
        [Header("Debug")] 
        public bool allowInspectorEdit = false;
		[Header("Properties")]
		public bool isPaused = false;
		public int simulationFrameRate = 60;
		[Range(0f, 1f)] public float dynamicRatio = 1f;
		public Vector3 gravity = new Vector3(0f, -10f, 0f);
		[Range(0f, 1f)] public float bounce = 0f;
		[Range(0f, 1f)] public float friction = 1f;

		[Header("Constraints")] public bool enableAngleLimits = true;
		public bool enableCollision = true;
		public bool enableLengthLimits = true;

		[Header("Ground Collision")] public bool collideWithGround = true;
		public float groundHeight = 0f;

		private SpringBone[] springBones;
		private SpringJobChild job;
		private int boneIndex, colIndex, lengthIndex;
		private NativeSlice<SpringBoneProperties> properties;
		private NativeSlice<SpringColliderProperties> colProperties;
		private NativeSlice<LengthLimitProperties> lengthProperties;

		// 初期化キャッシュ
		private SpringBoneProperties[] jobProperties;
		[SerializeField]
		private SpringColliderProperties[] jobComponents;
		[SerializeField]
		private SpringColliderProperties[] jobColProperties;
        #endregion


        #region PROPERTY
        public bool initialized { get; private set; }
        #endregion


        private static int GetObjectDepth(Transform inObject) {
			var depth = 0;
			var currentObject = inObject;
			while (currentObject != null) {
				currentObject = currentObject.parent;
				++depth;
			}
			return depth;
		}
		// Find SpringBones in children and assign them in depth order.
		// Note that the original list will be overwritten.
		public SpringBone[] FindSpringBones(bool includeInactive = false) {
			var unsortedSpringBones = GetComponentsInChildren<SpringBone>(includeInactive);
			var boneDepthList = unsortedSpringBones
				.Select(bone => new { bone, depth = GetObjectDepth(bone.transform) })
				.ToList();
			boneDepthList.Sort((a, b) => a.depth.CompareTo(b.depth));
			return boneDepthList.Select(item => item.bone).ToArray();
		}

		/// <summary>
		/// 初期化
		/// </summary>
		internal bool Initialize(SpringJobScheduler scheduler) {
			if (this.initialized)
				return false;

			this.initialized = true;

			this.springBones = this.FindSpringBones();

			var nSpringBones = this.springBones.Length;
			var propAlloc = scheduler.properties.Alloc(nSpringBones, out this.boneIndex, out this.properties);
			if (!propAlloc) {
				Debug.LogError("Spring Boneのボーン数上限が不足しています");
				this.Final(scheduler);
				return false;
			}

			// Colliders
			var colliders = this.GetComponentsInChildren<SpringCollider>(true);
			int nColliders = colliders.Length;
			var colPropAlloc = scheduler.colProperties.Alloc(nColliders, out this.colIndex, out this.colProperties);
			if (!colPropAlloc) {
				Debug.LogError("不明なエラー");
				this.Final(scheduler);
				return false;
			}
			for (int i = 0; i < nColliders; ++i) {
				colliders[i].index = i;
				Transform tr = colliders[i].transform;
				var comp = new SpringColliderProperties() {
					//layer = colliders[i].layer,
					type = colliders[i].type,
					radius = colliders[i].radius,
					width = colliders[i].width,
					height = colliders[i].height,
				};
				this.colProperties[i] = comp;
				scheduler.colliderTransforms[this.colIndex + i] = tr;

				// 本体についているコライダーの削除
				if (this.optimizeTransform)
					Object.DestroyImmediate(colliders[i]);
			}

			bool success = this.InitializeSpringBoneComponent(scheduler);
			if (!success) {
				this.Final(scheduler);
				return false;
			}

			var setting = new SpringBoneSettings() {
				dynamicRatio = this.dynamicRatio,
				gravity = this.gravity,
				bounce = this.bounce,
				friction = this.friction,
				enableAngleLimits = this.enableAngleLimits,
				enableCollision = this.enableCollision,
				enableLengthLimits = this.enableLengthLimits,
				collideWithGround = this.collideWithGround,
				groundHeight = this.groundHeight,
			};

			this.job.deltaTime = (this.simulationFrameRate > 0) ? (1f / this.simulationFrameRate) : 1f / 60f;
			this.job.settings = setting;

			this.job.nestedProperties = new NestedNativeSlice<SpringBoneProperties>(this.properties);
			this.job.nestedComponents = new NestedNativeSlice<SpringBoneComponents>(scheduler.components, this.boneIndex, this.properties.Length);
			this.job.nestedParentComponents = new NestedNativeSlice<Matrix4x4>(scheduler.parentComponents, this.boneIndex, this.properties.Length);
			this.job.nestedPivotComponents = new NestedNativeSlice<Matrix4x4>(scheduler.pivotComponents, this.boneIndex, this.properties.Length);
			this.job.nestedColliderProperties = new NestedNativeSlice<SpringColliderProperties>(this.colProperties);
			this.job.nestedColliderComponents = new NestedNativeSlice<SpringColliderComponents>(scheduler.colComponents, this.colIndex, this.colProperties.Length);
			this.job.nestedLengthLimitComponents = new NestedNativeSlice<Vector3>(scheduler.lengthComponents, this.lengthIndex, this.lengthProperties.Length);

			// Transformの階層構造をバラす
			if (this.optimizeTransform) {
				AnimatorUtility.OptimizeTransformHierarchy(this.gameObject, null);
				this.springBones = null;
			}

			return true;
		}

		/// <summary>
		/// 破棄
		/// </summary>
		internal void Final(SpringJobScheduler scheduler) {
			if (!this.initialized)
				return;

			// Clear Transform
			// NOTE: TransformをnullにしておくことでIJobParallelForTransformの負荷を下げる
			for (int i = 0; i < this.properties.Length; ++i) {
				int index = this.boneIndex + i;
				scheduler.boneTransforms[index] = null;
				scheduler.boneParentTransforms[index] = null;
				scheduler.bonePivotTransforms[index] = null;
				scheduler.colNumbers.Free(this.properties[i].collisionNumbers);
			}
			for (int i = 0; i < this.colProperties.Length; ++i)
				scheduler.colliderTransforms[this.colIndex + i] = null;
			for (int i = 0; i < this.lengthProperties.Length; ++i)
				scheduler.lengthLimitTransforms[this.lengthIndex + i] = null;

			// Poolへ返却
			scheduler.properties.Free(this.properties);
			scheduler.colProperties.Free(this.colProperties);
			scheduler.lengthProperties.Free(this.lengthProperties);

			this.initialized = false;
		}

		// This should be called by the SpringManager in its Awake function before any updates
		private bool InitializeSpringBoneComponent(SpringJobScheduler scheduler) {
			for (var i = 0; i < this.springBones.Length; ++i) {
				SpringBone springBone = this.springBones[i];
				springBone.index = i;

				var root = springBone.transform;
				var parent = root.parent;

				//var childPos = ComputeChildBonePosition(springBone);
				var childPos = springBone.ComputeChildPosition();
				var childLocalPos = root.InverseTransformPoint(childPos);
				var boneAxis = Vector3.Normalize(childLocalPos);

				var worldPos = root.position;
				//var worldRot = root.rotation;

				var springLength = Vector3.Distance(worldPos, childPos);
				var currTipPos = childPos;
				var prevTipPos = childPos;

				// Length Limit
				var nestedLengthLimitProps = new NestedNativeSlice<LengthLimitProperties>();
				var targetCount = springBone.lengthLimitTargets.Length;
				if (targetCount > 0) {
					bool lengthAlloc = scheduler.lengthProperties.Alloc(targetCount, out this.lengthIndex, out this.lengthProperties);
					if (!lengthAlloc) {
						Debug.LogError("Spring Boneの距離制限上限が不足しています");
						return false;
					}
					for (int m = 0; m < targetCount; ++m) {
						var targetRoot = springBone.lengthLimitTargets[m];
						int targetIndex = -1;
						if (targetRoot.TryGetComponent<SpringBone>(out var targetBone))
							targetIndex = targetBone.index;
						this.lengthProperties[i] = new LengthLimitProperties {
							targetIndex = targetIndex,
							target = Vector3.Magnitude(targetRoot.position - childPos),
						};
						scheduler.lengthLimitTransforms[this.lengthIndex + i] = targetRoot;
					}
					nestedLengthLimitProps = new NestedNativeSlice<LengthLimitProperties>(this.lengthProperties);
				}

				// Colliders Index
				var nestedCollisionNumbers = new NestedNativeSlice<int>();
				var nSpringBoneColliders = springBone.jobColliders.Length;
				if (nSpringBoneColliders > 0) {
					bool colNumAlloc = scheduler.colNumbers.Alloc(nSpringBoneColliders, out var colNumberIndex, out var collisionNumbers);
					if (!colNumAlloc) {
						Debug.LogError("Spring Boneのコリジョンインデックス上限が不足しています");
						return false;
					}
					for (int m = 0; m < nSpringBoneColliders; ++m)
						collisionNumbers[m] = springBone.jobColliders[m].index;

					nestedCollisionNumbers = new NestedNativeSlice<int>(collisionNumbers);
				}

				// ReadOnly
				int parentIndex = -1;
				Matrix4x4 pivotLocalMatrix = Matrix4x4.identity;
				if (parent.TryGetComponent<SpringBone>(out var parentBone))
					parentIndex = parentBone.index;

				var pivotIndex = -1;
				var pivotTransform = springBone.GetPivotTransform();
				var pivotBone = pivotTransform.GetComponentInParent<SpringBone>();
				if (pivotBone != null) {
					pivotIndex = pivotBone.index;
					// NOTE: PivotがSpringBoneの子供に置かれている場合の対処
					if (pivotBone.transform != pivotTransform) {
						// NOTE: 1個上の親がSpringBoneとは限らない
						//pivotLocalMatrix = Matrix4x4.TRS(pivotTransform.localPosition, pivotTransform.localRotation, Vector3.one);
						pivotLocalMatrix = Matrix4x4.Inverse(pivotBone.transform.localToWorldMatrix) * pivotTransform.localToWorldMatrix;
					}
				}

				// ReadOnly
				this.properties[i] = new SpringBoneProperties {
					stiffnessForce = springBone.stiffnessForce,
					dragForce = springBone.dragForce,
					springForce = springBone.springForce,
					windInfluence = springBone.windInfluence,
					angularStiffness = springBone.angularStiffness,
					yAngleLimits = new AngleLimitComponent {
						active = springBone.yAngleLimits.active,
						min = springBone.yAngleLimits.min,
						max = springBone.yAngleLimits.max,
					},
					zAngleLimits = new AngleLimitComponent {
						active = springBone.zAngleLimits.active,
						min = springBone.zAngleLimits.min,
						max = springBone.zAngleLimits.max,
					},
					radius = springBone.radius,
					boneAxis = boneAxis,
					springLength = springLength,
					localPosition = root.localPosition,
					initialLocalRotation = root.localRotation,
					parentIndex = parentIndex,

					pivotIndex = pivotIndex,
					pivotLocalMatrix = pivotLocalMatrix,

					collisionNumbers = nestedCollisionNumbers,
					lengthLimitProps = nestedLengthLimitProps,
				};

				// Read/Write (initialize param)
				scheduler.components[this.boneIndex + i] = new SpringBoneComponents {
					currentTipPosition = currTipPos,
					previousTipPosition = prevTipPos,
					localRotation = root.localRotation,
				};

				// TransformArray
				scheduler.boneTransforms[this.boneIndex + i] = root;
				scheduler.boneParentTransforms[this.boneIndex + i] = root.parent;
				scheduler.bonePivotTransforms[this.boneIndex + i] = pivotTransform;

				// turn off SpringBone component to let Job work
				springBone.enabled = false;

				if (this.optimizeTransform)
					Object.DestroyImmediate(springBone);
			}

			return true;
		}

		/// <summary>
		/// Jobデータの取得
		/// </summary>
		/// <returns></returns>
		public SpringJobChild GetJob() {
			if (this.isPaused)
				this.job.deltaTime = 0f;
			else
				this.job.deltaTime = (this.simulationFrameRate > 0) ? (1f / this.simulationFrameRate) : Time.deltaTime;
#if UNITY_EDITOR
			this.VerifySettings();
#endif
			return this.job;
		}

#if UNITY_EDITOR
		/// <summary>
		/// 設定の更新
		/// </summary>
		public void VerifySettings() {
			if (this.allowInspectorEdit && !this.optimizeTransform) {
				this.job.settings.dynamicRatio = this.dynamicRatio;
				this.job.settings.gravity = this.gravity;
				this.job.settings.bounce = this.bounce;
				this.job.settings.friction = this.friction;
				this.job.settings.enableAngleLimits = this.enableAngleLimits;
				this.job.settings.enableCollision = this.enableCollision;
				this.job.settings.enableLengthLimits = this.enableLengthLimits;
				this.job.settings.collideWithGround = this.collideWithGround;
				this.job.settings.groundHeight = this.groundHeight;

				for (int i = 0; i < this.springBones.Length; ++i) {
					var springBone = this.springBones[i];
					var prop = this.properties[i];
					prop.stiffnessForce = springBone.stiffnessForce;
					prop.dragForce = springBone.dragForce;
					prop.springForce = springBone.springForce;
					prop.windInfluence = springBone.windInfluence;
					prop.angularStiffness = springBone.angularStiffness;
					prop.yAngleLimits = new AngleLimitComponent {
						active = springBone.yAngleLimits.active,
						min = springBone.yAngleLimits.min,
						max = springBone.yAngleLimits.max,
					};
					prop.zAngleLimits = new AngleLimitComponent {
						active = springBone.zAngleLimits.active,
						min = springBone.zAngleLimits.min,
						max = springBone.zAngleLimits.max,
					};
					prop.radius = springBone.radius;

					this.properties[i] = prop;
				}
			}
		}
#endif

		void OnEnable() {
			// TODO: 毎回Initializeが呼ばれるので無駄
			SpringJobScheduler.Entry(this);
		}

		void OnDisable() {
			// TODO: 毎回Finalが呼ばれるので無駄
			SpringJobScheduler.Exit(this);
		}

		void OnDestroy() {
			SpringJobScheduler.Exit(this);
		}
	}
}
