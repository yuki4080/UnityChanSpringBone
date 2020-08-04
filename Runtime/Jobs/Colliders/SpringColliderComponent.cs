using UnityEngine;

namespace Unity.Animations.SpringBones.Jobs {
    /// <summary>
    /// コリジョン情報
    /// </summary>
    public struct Intersection {
        public Vector3 origin;
        public Vector3 upVector;
        public float radius;
    }

    /// <summary>
    /// コリジョン形状
    /// </summary>
    public enum ColliderType : int {
        Sphere,
        Panel,
        Capsule
    }

    /// <summary>
    /// コリジョンの設定値（ReadOnly in Job）
    /// </summary>
    [System.Serializable]
    public struct SpringColliderProperties {
        public int index;
        public int layer;
        public ColliderType type;
        public float radius;
        public float width;
        public float height;
    }

    /// <summary>
    /// 更新されるコリジョンの値（Read/Write in Job）
    /// </summary>
    [System.Serializable]
    public struct SpringColliderComponents {
        public Vector3 position;
        public Quaternion rotation;
        public Matrix4x4 worldToLocalMatrix;
        public Matrix4x4 localToWorldMatrix;
    }
}