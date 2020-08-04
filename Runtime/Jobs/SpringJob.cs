using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Animations.SpringBones.Jobs {
	/// <summary>
	/// SpringBoneManager設定
	/// </summary>
	public struct SpringBoneSettings {
		public float dynamicRatio;
		public Vector3 gravity;
		public float bounce;
		public float friction;
		public bool enableAngleLimits;
		public bool enableCollision;
		public bool enableLengthLimits;
		public bool collideWithGround;
		public float groundHeight;
	}

	/// <summary>
	/// ボーン毎の設定値（ReadOnly in Job）
	/// </summary>
	[System.Serializable]
	public struct SpringBoneProperties {
		public float stiffnessForce;
		public float dragForce;
		public Vector3 springForce;
		public float windInfluence;
		public float angularStiffness;
		public AngleLimitComponent yAngleLimits;
		public AngleLimitComponent zAngleLimits;
		public float radius;
		public float springLength;
		public Vector3 boneAxis;
		public int collisionMask;

		public int pivotIndex;  // PivotがSpringBoneだった場合のIndex（違う場合 -1）
		public int lengthLimitIndex;
		public int lengthLimitLength;

		public int parentIndex; // 親がSpringBoneだった場合のIndex（違う場合 -1）
		public Vector3 localPosition;
		public Quaternion initialLocalRotation;
	}

	/// <summary>
	/// 更新されるボーン毎の値（Read/Write in Job）
	/// </summary>
	[System.Serializable]
	public struct SpringBoneComponent {
		public Vector3 currentTipPosition;
		public Vector3 previousTipPosition;
		public Quaternion localRotation;
		public Vector3 position;
		public Quaternion rotation;
		public Vector3 parentPosition;
		public Quaternion parentRotation;
	}

	/// <summary>
	/// 距離制限の設定値
	/// </summary>
	[System.Serializable]
	public struct LengthLimitProperties {
		public int targetIndex; // targetがSpringBoneだった場合のIndex（違う場合 -1）
		public float target;
	}

	/// <summary>
	/// SpringBone計算ジョブ
	/// </summary>
	[Burst.BurstCompile]
	public struct SpringJob : IJob {
		[ReadOnly] public NativeArray<SpringBoneProperties> properties;
		[ReadOnly] public NativeArray<SpringColliderProperties> colProperties;
		[ReadOnly] public NativeArray<SpringColliderComponents> colComponents;
		[ReadOnly] public NativeArray<LengthLimitProperties> lengthProperties;
		[ReadOnly] public NativeArray<Vector3> lengthComponents;
		[ReadOnly] public NativeArray<Matrix4x4> pivotComponents;
		[ReadOnly] public NativeSlice<SpringJobChild> jobArray;

		public NativeArray<SpringBoneComponent> components;

		/// <summary>
		/// ジョブ実行
		/// </summary>
		void IJob.Execute() {
			for (int i = 0; i < this.jobArray.Length; ++i)
				this.jobArray[i].Execute(ref this);
		}
	}

	/// <summary>
	/// SpringManager単位の計算
	/// </summary>
	public partial struct SpringJobChild {
		public float deltaTime;
		public SpringBoneSettings settings;
		public int boneIndex, boneCount;
		public int colIndex, colCount;
		public int lengthIndex, lengthCount;

		/// <summary>
		/// ジョブ実行
		/// </summary>
		public void Execute(ref SpringJob job) {
			var length = this.boneCount;
			for (int i = 0; i < length; ++i) {
				var index = this.boneIndex + i;
				var bone = job.components[index];
				var prop = job.properties[index];

				if (prop.parentIndex >= 0) {
					// 親ノードがSpringBoneなら演算結果を反映する
					var parentBone = job.components[this.boneIndex + prop.parentIndex];
					bone.parentPosition = parentBone.position;
					bone.parentRotation = parentBone.rotation;
				}
				bone.position = bone.parentPosition + bone.parentRotation * prop.localPosition;
				bone.rotation = bone.parentRotation * bone.localRotation;
				Matrix4x4 pivotLocalToWorld;
				if (prop.pivotIndex >= 0) {
					var pivotBone = job.components[prop.pivotIndex];
					pivotLocalToWorld = Matrix4x4.TRS(pivotBone.position, pivotBone.rotation, Vector3.one);
				} else {
					pivotLocalToWorld = job.pivotComponents[index];
				}

				var baseWorldRotation = bone.parentRotation * prop.initialLocalRotation;
				this.UpdateSpring(ref bone, in prop, in baseWorldRotation);
				this.ResolveCollisionsAndConstraints(in job, ref bone, in prop, in pivotLocalToWorld);

				this.UpdateRotation(ref bone, in prop, in baseWorldRotation);
				job.components[index] = bone;
			}
		}

		private void UpdateSpring(ref SpringBoneComponent bone, in SpringBoneProperties prop, in Quaternion baseWorldRotation) {
			var orientedInitialPosition = bone.position + baseWorldRotation * prop.boneAxis * prop.springLength;

			// Hooke's law: force to push us to equilibrium
			var force = prop.stiffnessForce * (orientedInitialPosition - bone.currentTipPosition);
			force += prop.springForce + settings.gravity; // TODO: externalForce
			var sqrDt = this.deltaTime * this.deltaTime;
			force *= 0.5f * sqrDt;

			var temp = bone.currentTipPosition;
			force += (1f - prop.dragForce) * (bone.currentTipPosition - bone.previousTipPosition);
			bone.currentTipPosition += force;
			bone.previousTipPosition = temp;

			// Inlined because FixBoneLength is slow
			var headPosition = bone.position;
			var headToTail = bone.currentTipPosition - headPosition;
			var magnitude = Vector3.Magnitude(headToTail);

			const float MagnitudeThreshold = 0.001f;
			if (magnitude <= MagnitudeThreshold) {
				// was originally this
				//headToTail = transform.TransformDirection(boneAxis)
				Matrix4x4 mat = Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one);
				headToTail = mat.MultiplyVector(headToTail);
			} else {
				headToTail /= magnitude;
			}

			bone.currentTipPosition = headPosition + prop.springLength * headToTail;
		}

		private void ResolveCollisionsAndConstraints(in SpringJob job, ref SpringBoneComponent bone, in SpringBoneProperties prop, in Matrix4x4 pivotMat) {
			if (this.settings.enableLengthLimits)
				this.ApplyLengthLimits(in job, ref bone, in prop);

			var hadCollision = false;

			if (this.settings.collideWithGround)
				hadCollision = this.ResolveGroundCollision(ref bone, in prop);

			if (this.settings.enableCollision && !hadCollision)
				this.ResolveCollisions(in job, ref bone, in prop);

			if (this.settings.enableAngleLimits)
				this.ApplyAngleLimits(ref bone, in prop, in pivotMat);
		}

		// Returns the new tip position
		private void ApplyLengthLimits(in SpringJob job, ref SpringBoneComponent bone, in SpringBoneProperties prop) {
			var targetCount = prop.lengthLimitLength;
			if (targetCount == 0)
				return;

			const float SpringConstant = 0.5f;
			var accelerationMultiplier = SpringConstant * this.deltaTime * this.deltaTime;
			var movement = Vector3.zero;
			var start = prop.lengthLimitIndex;
			var length = start + prop.lengthLimitLength;
			for (int i = start; i < length; ++i) {
				int index = this.lengthIndex + i;
				var limit = job.lengthProperties[index];
				var lengthToLimitTarget = limit.target;
				var limitPosition = (limit.targetIndex >= 0) ? job.components[limit.targetIndex].position : job.lengthComponents[index];
				var currentToTarget = bone.currentTipPosition - limitPosition;
				//var currentDistanceSquared = Vector3.SqrMagnitude(currentToTarget);

				// Hooke's Law
				//var currentDistance = Mathf.Sqrt(currentDistanceSquared);
				var currentDistance = Vector3.Magnitude(currentToTarget);
				var distanceFromEquilibrium = currentDistance - lengthToLimitTarget;
				//movement -= accelerationMultiplier * distanceFromEquilibrium * Vector3.Normalize(currentToTarget);
				movement -= accelerationMultiplier * distanceFromEquilibrium * (currentToTarget / currentDistance);
			}

			bone.currentTipPosition += movement;
		}

		private bool ResolveGroundCollision(ref SpringBoneComponent bone, in SpringBoneProperties prop) {
			var groundHeight = this.settings.groundHeight;
			// Todo: this assumes a flat ground parallel to the xz plane
			var worldHeadPosition = bone.position;
			var worldTailPosition = bone.currentTipPosition;
			// NOTE: スケールが反映されないのだからスカラー値が欲しいなら行列演算する意味はないのでは…？
			var worldRadius = prop.radius;//transform.TransformDirection(prop.radius, 0f, 0f).magnitude;
			var worldLength = Vector3.Magnitude(bone.currentTipPosition - worldHeadPosition);
			worldHeadPosition.y -= groundHeight;
			worldTailPosition.y -= groundHeight;

			var collidingWithGround = SpringCollisionResolver.ResolvePanelOnAxis(
				worldHeadPosition, ref worldTailPosition, worldLength, worldRadius, SpringCollisionResolver.Axis.Y);

			if (collidingWithGround) {
				worldTailPosition.y += groundHeight;
				bone.currentTipPosition = FixBoneLength(ref bone, in prop, in worldTailPosition);
				// Todo: bounce, friction
				bone.previousTipPosition = bone.currentTipPosition;
			}

			return collidingWithGround;
		}

		private static Vector3 FixBoneLength(ref SpringBoneComponent bone, in SpringBoneProperties prop, in Vector3 tailPosition) {
			var minLength = 0.5f * prop.springLength;
			var maxLength = prop.springLength;
			var headPosition = bone.position;
			var headToTail = tailPosition - headPosition;
			var magnitude = headToTail.magnitude;

			const float MagnitudeThreshold = 0.001f;
			if (magnitude <= MagnitudeThreshold) {
				Matrix4x4 mat = Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one);
				return headPosition + mat.MultiplyVector(prop.boneAxis) * minLength;
			}

			var newMagnitude = (magnitude < minLength) ? minLength : magnitude;
			newMagnitude = (newMagnitude > maxLength) ? maxLength : newMagnitude;
			return headPosition + (newMagnitude / magnitude) * headToTail;
		}

		private bool ResolveCollisions(in SpringJob job, ref SpringBoneComponent bone, in SpringBoneProperties prop) {
			var desiredPosition = bone.currentTipPosition;
			var headPosition = bone.position;

			//            var scaledRadius = transform.TransformDirection(radius, 0f, 0f).magnitude;
			// var scaleMagnitude = new Vector3(prop.radius, 0f, 0f).magnitude;
			var hitNormal = new Vector3(0f, 0f, 1f);

			var hadCollision = false;

			var length = this.colCount;
			for (var i = 0; i < length; ++i) {
				var collider = job.colProperties[this.colIndex + i];
				var colliderTransform = job.colComponents[this.colIndex + i];

				// comment out for testing
				if ((prop.collisionMask & (1 << collider.layer)) == 0) {
					continue;
				}

				switch (collider.type) {
					case ColliderType.Capsule:
						hadCollision |= SpringCollisionResolver.ResolveCapsule(
							collider, colliderTransform,
							headPosition, ref bone.currentTipPosition, ref hitNormal,
							prop.radius);
						break;
					case ColliderType.Sphere:
						hadCollision |= SpringCollisionResolver.ResolveSphere(
							collider, colliderTransform,
							headPosition, ref bone.currentTipPosition, ref hitNormal, prop.radius);
						break;
					case ColliderType.Panel:
						hadCollision |= SpringCollisionResolver.ResolvePanel(
							collider, colliderTransform,
							headPosition, ref bone.currentTipPosition, ref hitNormal, prop.springLength,
							prop.radius);
						break;
				}
			}

			if (hadCollision) {
				var incidentVector = desiredPosition - bone.previousTipPosition;
				var reflectedVector = Vector3.Reflect(incidentVector, hitNormal);

				// friction
				var upwardComponent = Vector3.Dot(reflectedVector, hitNormal) * hitNormal;
				var lateralComponent = reflectedVector - upwardComponent;

				var bounceVelocity = this.settings.bounce * upwardComponent + (1f - this.settings.friction) * lateralComponent;
				const float BounceThreshold = 0.0001f;
				if (Vector3.SqrMagnitude(bounceVelocity) > BounceThreshold) {
					var distanceTraveled = Vector3.Magnitude(bone.currentTipPosition - bone.previousTipPosition);
					bone.previousTipPosition = bone.currentTipPosition - bounceVelocity;
					bone.currentTipPosition += Mathf.Max(0f, Vector3.Magnitude(bounceVelocity) - distanceTraveled) * Vector3.Normalize(bounceVelocity);
				} else {
					bone.previousTipPosition = bone.currentTipPosition;
				}
			}
			return hadCollision;
		}

		private void ApplyAngleLimits(ref SpringBoneComponent bone, in SpringBoneProperties prop, in Matrix4x4 parentLocalToWorld) {
			if (!prop.yAngleLimits.active && !prop.zAngleLimits.active)
				return;

			var origin = bone.position;
			var vector = bone.currentTipPosition - origin;

			var forward = parentLocalToWorld * -Vector3.right;

			var mulBack = parentLocalToWorld * Vector3.back;
			var mulDown = parentLocalToWorld * Vector3.down;

			if (prop.yAngleLimits.active) {
				prop.yAngleLimits.ConstrainVector(
					vector,
					mulDown, //parentLocalToWorldMat * -Vector3.up,
					mulBack, //parentLocalToWorldMat * -Vector3.forward,
					forward, prop.angularStiffness, this.deltaTime);
			}

			if (prop.zAngleLimits.active) {
				prop.zAngleLimits.ConstrainVector(
					vector,
					mulBack, //parentLocalToWorldMat * -Vector3.forward,
					mulDown, //parentLocalToWorldMat * -Vector3.up,
					forward, prop.angularStiffness, this.deltaTime);
			}

			bone.currentTipPosition = origin + vector;
		}

		private void UpdateRotation(ref SpringBoneComponent bone, in SpringBoneProperties prop, in Quaternion baseWorldRotation) {
			if (float.IsNaN(bone.currentTipPosition.x)
				| float.IsNaN(bone.currentTipPosition.y)
				| float.IsNaN(bone.currentTipPosition.z))
			{
				bone.currentTipPosition = bone.position + baseWorldRotation * prop.boneAxis * prop.springLength;
				bone.previousTipPosition = bone.currentTipPosition;
			}

			var actualLocalRotation = ComputeLocalRotation(in baseWorldRotation, ref bone, in prop);
			bone.localRotation = Quaternion.Lerp(bone.localRotation, actualLocalRotation, this.settings.dynamicRatio);
			bone.rotation = bone.parentRotation * bone.localRotation;
		}

		private Quaternion ComputeLocalRotation(in Quaternion baseWorldRotation, ref SpringBoneComponent bone, in SpringBoneProperties prop) {
			var worldBoneVector = bone.currentTipPosition - bone.position;
			var localBoneVector = Quaternion.Inverse(baseWorldRotation) * worldBoneVector;
			localBoneVector = Vector3.Normalize(localBoneVector);

			var aimRotation = Quaternion.FromToRotation(prop.boneAxis, localBoneVector);
			var outputRotation = prop.initialLocalRotation * aimRotation;

			return outputRotation;
		}
	}
}
