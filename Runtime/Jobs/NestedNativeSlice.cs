/************************************************
* NestedNativeSlice
* 
* Copyright (c) 2020 Yugo Fujioka
* 
* This software is released under the MIT License.
http://opensource.org/licenses/mit-license.php
*************************************************/

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace FUtility {
    /// <summary>
    /// NativeArray<NativeSlice>する為のNativeSliceラッパー
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public unsafe struct NestedNativeSlice<T> where T : struct {
        public IntPtr intPtr;
        public int stride;
        public int length;

        /// <summary>
        /// NativeSliceのラップ
        /// </summary>
        /// <param name="slice"></param>
        public NestedNativeSlice(NativeSlice<T> slice) {
            this.intPtr = new IntPtr(NativeSliceUnsafeUtility.GetUnsafeReadOnlyPtr(slice));
            this.stride = slice.Stride;
            this.length = slice.Length;
        }
        /// <summary>
        /// NativeArrayからのラップ
        /// </summary>
        /// <param name="array"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        public NestedNativeSlice(NativeArray<T> array, int startIndex, int length) {
            var slice = new NativeSlice<T>(array, startIndex, length);
            this.intPtr = new IntPtr(NativeSliceUnsafeUtility.GetUnsafeReadOnlyPtr(slice));
            this.stride = slice.Stride;
            this.length = slice.Length;
        }

        /// <summary>
        /// 自動コンバート
        /// </summary>
        /// <param name="nested"></param>
        public static implicit operator NativeSlice<T>(NestedNativeSlice<T> nested) {
            return nested.Convert();
        }

        /// <summary>
        /// NativeSliceの取り出し
        /// </summary>
        /// <returns></returns>
        public NativeSlice<T> Convert() {
            var intptr = this.intPtr.ToPointer();
            var slice = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<T>(intptr, this.stride, this.length);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeSliceUnsafeUtility.SetAtomicSafetyHandle(ref slice, AtomicSafetyHandle.GetTempUnsafePtrSliceHandle());
#endif
            return slice;
        }
    }
}
