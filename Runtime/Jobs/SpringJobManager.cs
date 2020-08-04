using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Unity.Animations.SpringBones.Jobs {
	public class SpringJobManager : MonoBehaviour {
        [Header("Optional Settings")] 
		public bool optimizeTransform = false;
        [Header("Debug")] 
        public bool allowInspectorEdit = false;
		[Header("Properties")]
		public bool isPaused = false;
		public int simulationFrameRate = 60;
		[Range(0f, 1f)] public float dynamicRatio = 0.5f;
		public Vector3 gravity = new Vector3(0f, -10f, 0f);
		[Range(0f, 1f)] public float bounce = 0f;
		[Range(0f, 1f)] public float friction = 1f;

		[Header("Constraints")] public bool enableAngleLimits = true;
		public bool enableCollision = true;
		public bool enableLengthLimits = true;

		[Header("Ground Collision")] public bool collideWithGround = true;
		public float groundHeight = 0f;

		// NOTE: Unity-Chan SpringBoneV2の設定をそのまま継続させる
		private SpringBone[] springBones;
		private SpringJobChild job;

		// ジョブ渡しバッファ
		private NativeSlice<SpringBoneProperties> properties;
		private NativeSlice<SpringColliderProperties> colProperties;
		private NativeSlice<LengthLimitProperties> lengthProperties;

		private bool initialized = false;

		/// <summary>
		/// 初期化
		/// </summary>
		internal void Initialize(SpringJobScheduler scheduler) {
			if (this.initialized)
				return;

			this.initialized = true;
			this.springBones = GetComponentsInChildren<SpringBone>(true);

			var nSpringBones = this.springBones.Length;

			scheduler.properties.Alloc(nSpringBones, out this.job.boneIndex, out this.properties);

			this.InitializeSpringBoneComponent(scheduler);

			// Colliders
			var colliders = this.GetComponentsInChildren<SpringCollider>(true);
			int nColliders = colliders.Length;
			scheduler.colProperties.Alloc(nColliders, out this.job.colIndex, out this.colProperties);
			for (int i = 0; i < nColliders; ++i) {
				Transform tr = colliders[i].transform;
				var comp = new SpringColliderProperties() {
                    layer = colliders[i].layer,
                    type = colliders[i].type,
                    radius = colliders[i].radius,
                    width = colliders[i].width,
                    height = colliders[i].height,
				};
				this.colProperties[i] = comp;
				scheduler.colliderTransforms[this.job.colIndex + i] = tr;

				if (this.optimizeTransform)
					Object.DestroyImmediate(colliders[i]);
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
			this.job.boneCount = nSpringBones;
			this.job.colCount = nColliders;
			this.job.lengthCount = this.lengthProperties.Length;

			// Transformの階層構造をバラす
			if (this.optimizeTransform) {
				AnimatorUtility.OptimizeTransformHierarchy(this.gameObject, null);
				this.springBones = null;
			}
		}

		/// <summary>
		/// 破棄
		/// </summary>
		internal void Final(SpringJobScheduler scheduler) {
			if (!this.initialized)
				return;

			// Clear Transform
			// NOTE: TransformをnullにしておくことでIJobParallelForTransformの負荷を下げる
			for (int i = this.job.boneIndex; i < this.job.boneIndex + this.job.boneCount; ++i) {
				scheduler.boneTransforms[i] = null;
				scheduler.boneParentTransforms[i] = null;
				scheduler.bonePivotTransforms[i] = null;
			}
			for (int i = this.job.colIndex; i < this.job.colIndex + this.job.colCount; ++i)
				scheduler.colliderTransforms[i] = null;
			for (int i = this.job.lengthIndex; i < this.job.lengthIndex + this.job.lengthCount; ++i)
				scheduler.lengthLimitTransforms[i] = null;

			// Poolへ返却
			scheduler.properties.Free(this.properties);
			scheduler.colProperties.Free(this.colProperties);
			scheduler.lengthProperties.Free(this.lengthProperties);

			this.initialized = false;
		}

		// This should be called by the SpringManager in its Awake function before any updates
		private void InitializeSpringBoneComponent(SpringJobScheduler scheduler) {
			List<Transform> lengthTargetList = new List<Transform>(256);
			List<LengthLimitProperties> lengthLimitList = new List<LengthLimitProperties>(256);

			for (var i = 0; i < this.springBones.Length; ++i) {
				SpringBone springBone = this.springBones[i];
				springBone.index = i;

				var root = springBone.transform;
				var parent = root.parent;

				var childPos = ComputeChildBonePosition(springBone);
				var childLocalPos = root.InverseTransformPoint(childPos);
				var boneAxis = Vector3.Normalize(childLocalPos);

				var worldPos = root.position;
				//var worldRot = root.rotation;

				var springLength = Vector3.Distance(worldPos, childPos);
				var currTipPos = childPos;
				var prevTipPos = childPos;

				// Length Limit
				var targetListIndex = lengthTargetList.Count;
				var targetCount = springBone.lengthLimitTargets.Length;
				//lengthsToLimitTargets = new float[targetCount];
				for (int target = 0; target < targetCount; ++target) {
					Transform targetRoot = springBone.lengthLimitTargets[target];
					lengthTargetList.Add(targetRoot);
					int targetIndex = -1;
					if (targetRoot.TryGetComponent<SpringBone>(out var targetBone))
						targetIndex = targetBone.index;
					var lengthLimit = new LengthLimitProperties {
						targetIndex = targetIndex,
						////position = targetRoot.position,
						target = Vector3.Magnitude(targetRoot.position - childPos),
					};
					lengthLimitList.Add(lengthLimit);
				}

				// ReadOnly
				int parentIndex = -1, pivotIndex = -1;
				if (parent.TryGetComponent<SpringBone>(out var parentBone))
					parentIndex = parentBone.index;
				var pivotTransform = springBone.GetPivotTransform();
				if (pivotTransform.TryGetComponent<SpringBone>(out var pivotBone))
					pivotIndex = pivotBone.index;
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
                    collisionMask = springBone.collisionMask,

					pivotIndex = pivotIndex,
					lengthLimitIndex = targetListIndex,
					lengthLimitLength = targetCount,
					
					parentIndex = parentIndex,
					localPosition = root.localPosition,
					initialLocalRotation = root.localRotation,
				};

				// Read/Write (initialize param)
				scheduler.components[this.job.boneIndex + i] = new SpringBoneComponent {
					currentTipPosition = currTipPos,
					previousTipPosition = prevTipPos,
					localRotation = root.localRotation,
				};

				// TransformArray
				scheduler.boneTransforms[this.job.boneIndex + i] = root;
				scheduler.boneParentTransforms[this.job.boneIndex + i] = root.parent;
				scheduler.bonePivotTransforms[this.job.boneIndex + i] = pivotTransform;

				// turn off SpringBone component to let Job work
				springBone.enabled = false;

				if (this.optimizeTransform)
					Object.DestroyImmediate(springBone);
			}

			// LengthLimit
			// NOTE: Inspector拡張で静的にバッファ用意した方がベター
			int nLengthLimits = lengthTargetList.Count;
			scheduler.lengthProperties.Alloc(nLengthLimits, out this.job.lengthIndex, out this.lengthProperties);
			for (int i = 0; i < nLengthLimits; ++i) {
				scheduler.lengthLimitTransforms[this.job.lengthIndex + i] = lengthTargetList[i];
				this.lengthProperties[i] = lengthLimitList[i];
			}
		}

		private static Vector3 ComputeChildBonePosition(SpringBone bone) {
			var children = GetValidSpringBoneChildren(bone.transform);
			var childCount = children.Count;

			if (childCount == 0) {
				// This should never happen
				Debug.LogWarning("SpringBone「" + bone.name + "」に有効な子供がありません");
				return bone.transform.position + bone.transform.right * -0.1f;
			}

			if (childCount == 1) {
				return children[0].position;
			}

			var initialTailPosition = new Vector3(0f, 0f, 0f);
			var averageDistance = 0f;
			var selfPosition = bone.transform.position;
			for (int childIndex = 0; childIndex < childCount; childIndex++) {
				var childPosition = children[childIndex].position;
				initialTailPosition += childPosition;
				averageDistance += (childPosition - selfPosition).magnitude;
			}

			averageDistance /= childCount;
			initialTailPosition /= childCount;
			var selfToInitial = initialTailPosition - selfPosition;
			selfToInitial.Normalize();
			initialTailPosition = selfPosition + averageDistance * selfToInitial;
			return initialTailPosition;
		}

		private static IList<Transform> GetValidSpringBoneChildren(Transform parent) {
			// Ignore SpringBonePivots
			var childCount = parent.childCount;
			var children = new List<Transform>(childCount);
			for (int childIndex = 0; childIndex < childCount; childIndex++) {
				var child = parent.GetChild(childIndex);
				if (child.GetComponent<SpringBonePivot>() == null) {
					children.Add(child);
				}
			}

			return children;
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
