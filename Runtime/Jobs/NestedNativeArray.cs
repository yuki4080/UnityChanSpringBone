using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace FUtility {
    /// <summary>
    /// NativeArray<NativeArray>する為のラッパー
    /// スレッドセーフかどうかのチェックを回避する為だけのもの
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public unsafe struct NestedNativeArray<T> where T : struct {
        private void* ptr;
        public int length;

        /// <summary>
        /// NativeArrayからのラップ
        /// </summary>
        /// <param name="array"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        public NestedNativeArray(NativeArray<T> array, int startIndex, int length) {
            if (array.Length == 0 || length == 0) {
                this.ptr = null;
                this.length = 0;
                return;
            }
            var subArray = array.GetSubArray(startIndex, length);
            this.ptr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(subArray);
            this.length = subArray.Length;
        }

        public int Length => this.length;

        /// <summary>
        /// NativeArrayのように使える
        /// 結局こうなってしまうのか感
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public T this[int index] {
            get {
                //CheckElementReadAccess(index);
                return UnsafeUtility.ReadArrayElement<T>(this.ptr, index);
            }

            [WriteAccessRequired]
            set {
                //CheckElementWriteAccess(index);
                UnsafeUtility.WriteArrayElement(this.ptr, index, value);
            }
        }
        public unsafe void* GetUnsafeReadOnlyPtr() {
            return this.ptr;
        }
    //        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
    //        void CheckElementReadAccess(int index) {
    //            if (index < 0
    //                && index >= m_Length) {
    //                throw new IndexOutOfRangeException($"Index {index} is out of range (must be between 0 and {m_Length - 1}).");
    //            }

    //#if ENABLE_UNITY_COLLECTIONS_CHECKS
    //            var versionPtr = (int*)m_Safety.versionNode;
    //            if (m_Safety.version != ((*versionPtr) & AtomicSafetyHandle.ReadCheck))
    //                AtomicSafetyHandle.CheckReadAndThrowNoEarlyOut(m_Safety);
    //#endif
    //        }
    }
}
