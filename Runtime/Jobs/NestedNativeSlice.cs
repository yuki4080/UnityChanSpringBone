using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

/// <summary>
/// yugofujioka/NestedNativeSlice is licensed under the
/// MIT License
/// A short and simple permissive license with conditions only requiring preservation of copyright and license notices.
/// Licensed works, modifications, and larger works may be distributed under different terms and without source code.
/// </summary>
namespace Unity.Animations.SpringBones.Jobs {
    public unsafe struct NestedNativeSlice<T> where T : struct {
        public IntPtr intPtr;
        public int stride;
        public int length;

        public NestedNativeSlice(NativeSlice<T> slice) {
            this.intPtr = new IntPtr(NativeSliceUnsafeUtility.GetUnsafeReadOnlyPtr(slice));
            this.stride = slice.Stride;
            this.length = slice.Length;
        }
        public NestedNativeSlice(NativeArray<T> array, int startIndex, int length) {
            var slice = new NativeSlice<T>(array, startIndex, length);
            this.intPtr = new IntPtr(NativeSliceUnsafeUtility.GetUnsafeReadOnlyPtr(slice));
            this.stride = slice.Stride;
            this.length = slice.Length;
        }

        public static implicit operator NativeSlice<T>(NestedNativeSlice<T> nested) {
            return nested.Convert();
        }

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
