using UnityEngine;
using UnityEditor;
using System.Collections.Generic;


namespace Unity.Animations.SpringBones.Jobs {
	public static class SpringJobConvertAction {
		[MenuItem("UTJ/選択したオブジェクトのSpringBoneをJob化")]
		public static void SwitchSpringJob() {
			if (EditorApplication.isPlaying || Application.isPlaying && EditorApplication.isCompiling)
				return;

			if (EditorUtility.DisplayDialog("Attention Please !", "Job化したSpring Boneは元に戻せません。\nこのまま実行してよろしいですか？", "いいえ", "はい"))
				return;

			var activeObject = Selection.activeGameObject;

			var bones = activeObject.GetComponentsInChildren<SpringBone>(true);
			List<SpringCollider> jobColliderList = new List<SpringCollider>(128);
			foreach (var bone in bones) {
				jobColliderList.Clear();

				// Colliderのコンバート
				for (int i = 0; i < bone.capsuleColliders.Length; ++i) {
					var col = bone.capsuleColliders[i];
					if (!col.TryGetComponent<SpringCollider>(out var jobCol)) {
						jobCol = col.gameObject.AddComponent<SpringCollider>();
						jobCol.type = ColliderType.Capsule;
						jobCol.radius = col.radius;
						jobCol.height = col.height;
					}
					jobColliderList.Add(jobCol);
				}
				for (int i = 0; i < bone.sphereColliders.Length; ++i) {
					var col = bone.sphereColliders[i];
					if (!col.TryGetComponent<SpringCollider>(out var jobCol)) {
						jobCol = col.gameObject.AddComponent<SpringCollider>();
						jobCol.type = ColliderType.Sphere;
						jobCol.radius = col.radius;
					}
					jobColliderList.Add(jobCol);
				}
				for (int i = 0; i < bone.panelColliders.Length; ++i) {
					var col = bone.panelColliders[i];
					if (!col.TryGetComponent<SpringCollider>(out var jobCol)) {
						jobCol = col.gameObject.AddComponent<SpringCollider>();
						jobCol.type = ColliderType.Panel;
						jobCol.radius = col.width;
						jobCol.height = col.height;
					}
					jobColliderList.Add(jobCol);
				}
				// NOTE: SerializeFieldなのでnullにしてもゼロ配列が入る
				bone.capsuleColliders = null;
				bone.sphereColliders = null;
				bone.panelColliders = null;

				// NOTE: SerializeFieldなので反映しないと実行時に消される
				var so = new SerializedObject(bone);
				so.FindProperty("enabledJobSystem").boolValue = true; // Job化したら編集不可にする為のフラグ
				var prop = so.FindProperty("jobColliders");
				prop.arraySize = jobColliderList.Count;
				for (int i = 0; i < jobColliderList.Count; ++i)
					prop.GetArrayElementAtIndex(i).objectReferenceValue = jobColliderList[i];
				so.SetIsDifferentCacheDirty(); // シーンセーブせずにCtrl+Dで複製した場合の対処
				so.ApplyModifiedProperties();  // SerializedFieldの反映

				var scheduler = Object.FindObjectOfType<SpringJobScheduler>();
				if (scheduler == null) {
					var go = new GameObject("SpringJobScheduler(Don't destroy)");
					go.AddComponent<SpringJobScheduler>();
				}
			}
			// LegacyColliderの削除
			var sphere = activeObject.GetComponentsInChildren<SpringSphereCollider>(true);
			foreach (var s in sphere)
				Object.DestroyImmediate(s);
			var capsule = activeObject.GetComponentsInChildren<SpringCapsuleCollider>(true);
			foreach (var s in capsule)
				Object.DestroyImmediate(s);
			var panel = activeObject.GetComponentsInChildren<SpringPanelCollider>(true);
			foreach (var s in panel)
				Object.DestroyImmediate(s);

			// SpringManagerのコンバート
			var managers = activeObject.GetComponentsInChildren<SpringManager>(true);
			foreach (var manager in managers) {
				if (!manager.gameObject.TryGetComponent<SpringJobManager>(out var jobManager)) {
					jobManager = manager.gameObject.AddComponent<SpringJobManager>();
				}
				//jobManager.dynamicRatio = manager.dynamicRatio;
				jobManager.dynamicRatio = 1f; // 従来版とJob版で取り扱いが異なる
				jobManager.gravity = manager.gravity;
				jobManager.bounce = manager.bounce;
				jobManager.friction = manager.friction;
				jobManager.enableAngleLimits = manager.enableAngleLimits;
				jobManager.enableCollision = manager.enableCollision;
				jobManager.enableLengthLimits = manager.enableLengthLimits;
				jobManager.collideWithGround = manager.collideWithGround;
				jobManager.groundHeight = manager.groundHeight;

				Object.DestroyImmediate(manager);

				jobManager.CachedJobParam();
				var so = new SerializedObject(jobManager.gameObject);
				so.SetIsDifferentCacheDirty(); // シーンセーブせずにCtrl+Dで複製した場合の対処
				so.ApplyModifiedProperties();  // SerializedFieldの反映
			}

		}
	}
}
