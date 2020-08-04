using UnityEngine;
using UnityEditor;


namespace Unity.Animations.SpringBones.Jobs {
	public static class SpringJobConvertAction {
		[MenuItem("UTJ/選択したオブジェクトのSpringBoneをJob化")]
		public static void ConvertSpringJob() {
			if (EditorApplication.isPlaying || Application.isPlaying && EditorApplication.isCompiling)
				return;

			var activeObject = Selection.activeGameObject;

			// SpringManager
			var managers = activeObject.GetComponentsInChildren<SpringManager>(true);
			foreach (var manager in managers) {
				if (!manager.gameObject.TryGetComponent<SpringJobManager>(out var jobManager)) {
					jobManager = manager.gameObject.AddComponent<SpringJobManager>();
				}
				jobManager.dynamicRatio = manager.dynamicRatio;
				jobManager.gravity = manager.gravity;
				jobManager.bounce = manager.bounce;
				jobManager.friction = manager.friction;
				jobManager.enableAngleLimits = manager.enableAngleLimits;
				jobManager.enableCollision = manager.enableCollision;
				jobManager.enableLengthLimits = manager.enableLengthLimits;
				jobManager.collideWithGround = manager.collideWithGround;
				jobManager.groundHeight = manager.groundHeight;

				Object.DestroyImmediate(manager);
			}
			// SpringCollider
			var spheres = activeObject.GetComponentsInChildren<SpringSphereCollider>(true);
			foreach (var sphere in spheres) {
				if (!sphere.gameObject.TryGetComponent<SpringCollider>(out var collider)) {
					collider = sphere.gameObject.AddComponent<SpringCollider>();
				}
				collider.type = ColliderType.Sphere;
				collider.radius = sphere.radius;

				Object.DestroyImmediate(sphere);
			}
			// CapsulegCollider
			var capsules = activeObject.GetComponentsInChildren<SpringCapsuleCollider>(true);
			foreach (var capsule in capsules) {
				if (!capsule.gameObject.TryGetComponent<SpringCollider>(out var collider)) {
					collider = capsule.gameObject.AddComponent<SpringCollider>();
				}
				collider.type = ColliderType.Capsule;
				collider.radius = capsule.radius;
				collider.height = capsule.height;

				Object.DestroyImmediate(capsule);
			}
			// PanelCollider
			var panels = activeObject.GetComponentsInChildren<SpringPanelCollider>(true);
			foreach (var panel in panels) {
				if (!panel.gameObject.TryGetComponent<SpringCollider>(out var collider)) {
					collider = panel.gameObject.AddComponent<SpringCollider>();
				}
				collider.type = ColliderType.Panel;
				collider.width = panel.width;
				collider.height = panel.height;

				Object.DestroyImmediate(panel);
			}
		}
	}
}
