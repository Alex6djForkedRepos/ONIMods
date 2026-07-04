/*
 * Copyright 2026 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Collections;
using UnityEngine.Pool;

namespace PeterHan.FastTrack.PathPatches {
	/// <summary>
	/// Updates nav grids much faster by avoiding duplicates and using a more efficient way
	/// to store the cells.
	/// </summary>
	internal sealed class NavGridGraphUpdater : IDisposable {
		/// <summary>
		/// The singleton instance of this class.
		/// </summary>
		internal static NavGridGraphUpdater Instance { get; private set; }

		/// <summary>
		/// Creates the singleton instance of this class.
		/// </summary>
		internal static void CreateInstance() {
			Instance = new NavGridGraphUpdater();
		}

		/// <summary>
		/// Destroys the singleton instance of this class.
		/// </summary>
		internal static void DestroyInstance() {
			Instance.Dispose();
			Instance = null;
		}

		/// <summary>
		/// The pool of available temporary bit arrays for UpdatePickups. Must be concurrent
		/// as it is called in parallel by async path optimizations.
		/// </summary>
		private readonly ObjectPool<BitArray> pool;

		internal NavGridGraphUpdater() {
			int cc = Grid.CellCount;
			pool = new ObjectPool<BitArray>(() => new BitArray(cc), null, (entry) =>
				entry.SetAll(false), null, false, 10, 256);
		}

		public void Dispose() {
			pool.Dispose();
		}

		/// <summary>
		/// Updates the specified nav grid.
		/// </summary>
		/// <param name="instance">The nav grid to update.</param>
		internal void UpdateGraph(NavGrid instance) {
			var dirty = instance.DirtyCells;
			int n = dirty.Count, rx = instance.updateRangeX, ry = instance.updateRangeY;
			var flags = instance.DirtyBitFlags;
			var totalDirty = pool.Get();
			var newList = NavGrid.dirtyCellsSwapBuffer;
			for (int i = 0; i < n; i++) {
				int cell = dirty[i];
				Grid.CellToXY(cell, out int x, out int y);
				int minX = Grid.ClampX(x - rx), minY = Grid.ClampY(y - ry),
					sizeX = Grid.ClampX(x + rx) - minX + 1,
					sizeY = Grid.ClampY(y + ry) - minY + 1, step = Grid.WidthInCells - sizeX;
				// Clear dirty bit array because the expanded set tracks it automatically
				flags[cell >> 3] = 0;
				cell = Grid.XYToCell(minX, minY);
				while (sizeY-- > 0) {
					// Add only unique cells to the expanded list
					for (int j = sizeX; j > 0; j--) {
						if (!totalDirty.Get(cell)) {
							totalDirty.Set(cell, true);
							newList.Add(cell);
						}
						cell++;
					}
					cell += step;
				}
			}
			dirty.Clear();
			pool.Release(totalDirty);
			instance.UpdateGraph(newList);
			newList.Clear();
		}
	}
}
