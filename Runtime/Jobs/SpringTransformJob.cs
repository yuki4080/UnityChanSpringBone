using UnityEngine;
using UnityEngine.Jobs;
using Unity.Collections;


namespace Unity.Animations.SpringBones.Jobs {
	/// <summary>
	/// SpringBone計算の反映
	/// </summary>
	[Burst.BurstCompile]
	public struct SpringBoneApplyJob : IJobParallelForTransform {
		[ReadOnly] public NativeArray<SpringBoneComponent> components;

		void IJobParallelForTransform.Execute(int index, TransformAccess transform) {
			// Apply
			transform.localRotation = this.components[index].localRotation;
		}
	}

	/// <summary>
	/// 親ノードの情報更新
	/// </summary>
	[Burst.BurstCompile]
	public struct SpringParentJob : IJobParallelForTransform {
		//[ReadOnly] public NativeArray<SpringBoneProperties> properties;

		public NativeArray<SpringBoneComponent> components;

		void IJobParallelForTransform.Execute(int index, TransformAccess transform) {
			// NOTE: 余計な判定を入れない方が速い模様
			//SpringBoneProperties prop = this.properties[index];
			//if (prop.parentIndex < 0) {
			SpringBoneComponent bone = this.components[index];
				bone.parentPosition = transform.position;
				bone.parentRotation = transform.rotation;
				this.components[index] = bone;
			//}
		}
	}

	/// <summary>
	/// Pivot位置の更新
	/// </summary>
	[Burst.BurstCompile]
	public struct SpringPivotJob : IJobParallelForTransform {
		//[ReadOnly] public NativeSlice<SpringJobChild> jobArray;
		//[ReadOnly] public NativeArray<SpringBoneProperties> properties;

		[WriteOnly] public NativeArray<Matrix4x4> components;

		void IJobParallelForTransform.Execute(int index, TransformAccess transform) {
			// NOTE: 余計な判定を入れない方が速い模様
			//int jobIndex = -1;
			//for (int i = 0; i < this.jobArray.Length; ++i) {
			//	if (this.jobArray[i].boneIndex <= index && index < this.jobArray[i].boneIndex + this.jobArray[i].boneCount) {
			//		jobIndex = i;
			//		break;
			//	}
			//}
			//if (jobIndex < 0) {
			//	// NOTE: DeactiveなTransformは送られてこない、かつDeactive時にnullを入れているので正常動作としてはここに来ない
			//	Debug.LogWarning("Skip Transform!");
			//	return;
			//}

			//if (this.jobArray[jobIndex].settings.enableAngleLimits) {
			//	SpringBoneProperties prop = this.properties[index];
			//	if (prop.pivotIndex < 0 && (prop.yAngleLimits.active || prop.zAngleLimits.active)) {
			//		this.components[index] = transform.localToWorldMatrix;
			//	}
			//}
			this.components[index] = transform.localToWorldMatrix;
		}
	}

	/// <summary>
	/// コリジョンの更新
	/// </summary>
	[Burst.BurstCompile]
	public struct SpringColliderJob : IJobParallelForTransform {
		[ReadOnly] public NativeSlice<SpringJobChild> jobArray;
		
		[WriteOnly] public NativeArray<SpringColliderComponents> components;

		void IJobParallelForTransform.Execute(int index, TransformAccess transform) {
			int jobIndex = -1;
			for (int i = 0; i < this.jobArray.Length; ++i) {
				if (this.jobArray[i].colIndex <= index && index < this.jobArray[i].colIndex + this.jobArray[i].colCount) {
					jobIndex = i;
					break;
				}
			}
			if (jobIndex < 0) {
				// NOTE: DeactiveなTransformは送られてこない、かつDeactive時にnullを入れているので正常動作としてはここに来ない
				Debug.LogWarning("Skip Transform!");
				return;
			}

			var job = this.jobArray[jobIndex];
			if (job.settings.enableCollision) {
				this.components[index] = new SpringColliderComponents {
					position = transform.position,
					rotation = transform.rotation,
					localToWorldMatrix = transform.localToWorldMatrix,
					worldToLocalMatrix = transform.worldToLocalMatrix,
				};
			}
		}
	}

	/// <summary>
	/// 距離制限座標の更新
	/// </summary>
	[Burst.BurstCompile]
	public struct SpringLengthLimitJob : IJobParallelForTransform {
		[ReadOnly] public NativeSlice<SpringJobChild> jobArray;
		[ReadOnly] public NativeSlice<LengthLimitProperties> properties;

		[WriteOnly] public NativeArray<Vector3> components;

		void IJobParallelForTransform.Execute(int index, TransformAccess transform) {
			int jobIndex = -1;
			for (int i = 0; i < this.jobArray.Length; ++i) {
				if (this.jobArray[i].colIndex <= index && index < this.jobArray[i].colIndex + this.jobArray[i].colCount) {
					jobIndex = i;
					break;
				}
			}
			if (jobIndex < 0) {
				// NOTE: DeactiveなTransformは送られてこない、かつDeactive時にnullを入れているので正常動作としてはここに来ない
				Debug.LogWarning("Skip Transform!");
				return;
			}

			var job = this.jobArray[jobIndex];
			if (job.settings.enableLengthLimits) {
				if (this.properties[index].targetIndex >= 0)
					return;
				this.components[index] = transform.position;
			}
		}
	}
}
