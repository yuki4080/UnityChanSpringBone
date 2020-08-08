/************************************************
* NativeContainerPool
* 
* Copyright (c) 2020 Yugo Fujioka
* 
* This software is released under the MIT License.
http://opensource.org/licenses/mit-license.php
*************************************************/

using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace FUtility {
    /// <summary>
    /// NativeArray内のブロックパラメータ
    /// </summary>
    internal unsafe struct NativeBlock {
        public int startIndex;
        public int size;
        public void* ptr;
    }

    /// <summary>
    /// NativeArrayをNativeSliceでブロック化
    /// </summary>
    public class NativeContainerPool<T> where T : struct {
        private NativeArray<T> array;
        private TaskSystem<NativeBlock> freePool;
        private TaskSystem<NativeBlock> usedPool;

        private MatchHandler<NativeBlock> getFreeBlockHandler;
        private MatchHandler<NativeBlock> getusedBlockHandler;
        private MatchHandler<NativeBlock> connectBlockTopHandler;
        private MatchHandler<NativeBlock> connectBlockEndHandler;

        public NativeArray<T> nativeArray => this.array;

        /// <summary>
        /// NativeArrayのPool
        /// </summary>
        /// <param name="arraySize">全体Arrayサイズ</param>
        /// <param name="blockCapacity">確保ブロック最大数</param>
        public NativeContainerPool(int arraySize, int blockCapacity) {
            if (arraySize < 0 || blockCapacity <= 0) {
                Debug.LogError("登録数が無効です");
                return;
            }
            this.array = new NativeArray<T>(arraySize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            this.freePool = new TaskSystem<NativeBlock>(blockCapacity);
            this.usedPool = new TaskSystem<NativeBlock>(blockCapacity);

            this.getFreeBlockHandler = new MatchHandler<NativeBlock>(this.GetFreeBlock);
            this.getusedBlockHandler = new MatchHandler<NativeBlock>(this.GetUsedBlock);
            this.connectBlockTopHandler = new MatchHandler<NativeBlock>(this.ConnectBlockTop);
            this.connectBlockEndHandler = new MatchHandler<NativeBlock>(this.ConnectBlockEnd);

            var block = new NativeBlock { startIndex = 0, size = arraySize };
            this.freePool.Attach(block);
        }

        public void Dispose() {
            this.array.Dispose();
            this.freePool.Clear();
            this.usedPool.Clear();
        }

        /// <summary>
        /// NativeSliceの取得
        /// </summary>
        /// <param name="size">要求サイズ</param>
        /// <returns>確保成功</returns>
        public unsafe bool Alloc(int size, out int index, out NativeSlice<T> slice) {
            if (size == 0) {
                index = 0;
                slice = default;
                return false;
            }

            NativeBlock block;
            this.needSize = size;
            if (this.freePool.Pickup(this.getFreeBlockHandler, out block)) {
                var memory = new NativeSlice<T>(this.array, block.startIndex, size);
                index = block.startIndex;
                slice = memory;

                var newBlock = new NativeBlock { startIndex = block.startIndex, size = size, ptr = memory.GetUnsafeReadOnlyPtr() };
                this.usedPool.Attach(newBlock);

                block.startIndex += size;
                block.size -= size;
                this.freePool.Attach(block);

                return true;
            }
            index = -1;
            slice = default;
            return false;
        }
        private int needSize = 0;
        private int GetFreeBlock(NativeBlock block) {
            if (block.size >= needSize)
                return 1;
            return 0;
        }

        /// <summary>
        /// NativeSliceの返却
        /// </summary>
        /// <param name="slice">確保したNativeSlice</param>
        public unsafe void Free(NativeSlice<T> slice) {
            if (slice.Stride == 0 || slice.Length == 0)
                return;

            this.freeAddress = slice.GetUnsafeReadOnlyPtr<T>();
            // 使用中のブロックから回収
            if (this.usedPool.Pickup(this.getusedBlockHandler, out this.connectBlock)) {
                NativeBlock block;
                // 空きブロックの後ろ接続
                if (this.freePool.Pickup(this.connectBlockTopHandler, out block)) {
                    block.size += this.connectBlock.size;
                    this.connectBlock = block;
                }
                // 空きブロックの先頭接続
                if (this.freePool.Pickup(this.connectBlockEndHandler, out block)) {
                    this.connectBlock.size += block.size;
                }
                this.connectBlock.ptr = null; // safe delete
                this.freePool.Attach(this.connectBlock);
            }
        }
        private unsafe void* freeAddress = null;
        private unsafe int GetUsedBlock(NativeBlock block) {
            // NOTE: 数十程度のブロック数を想定しているので全走査
            if (this.freeAddress == block.ptr)
                return 1;
            return 0;
        }
        private NativeBlock connectBlock;
        private unsafe int ConnectBlockTop(NativeBlock block) {
            if (this.connectBlock.startIndex == (block.startIndex + block.size))

                return 1; 
            return 0;
        }
        private unsafe int ConnectBlockEnd(NativeBlock block) {
            if (block.startIndex == (this.connectBlock.startIndex + this.connectBlock.size))
                return 1;
            return 0;
        }
    }
}
