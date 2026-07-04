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
using System.Collections.Concurrent;

namespace PeterHan.FastTrack.PathPatches {
	/// <summary>
	/// Caches global pathfind requests, drastically reducing work by avoiding repathing when
	/// nothing has changed.
	/// </summary>
	public static class PathCacher {
		/// <summary>
		/// The number of scaled in-game seconds that will pass before the path cache is
		/// automatically invalidated for an entity.
		/// </summary>
		public const double INVALIDATE_TIME = 6.0;

		/// <summary>
		/// The current frame time.
		/// </summary>
		private static double now;

		/// <summary>
		/// Pools the cache
		/// </summary>
		private static readonly ConcurrentQueue<CacheData> POOL =
			new ConcurrentQueue<CacheData>();

		/// <summary>
		/// Map path cache IDs to path cache values.
		/// </summary>
		private static readonly ConcurrentDictionary<PathGrid, CacheData> PATH_CACHE =
			new ConcurrentDictionary<PathGrid, CacheData>(4, 128);
		
		/// <summary>
		/// Checks to see if the path cache is clean.
		/// </summary>
		/// <param name="grid">The grid that is querying.</param>
		/// <param name="cell">The root cell that will be used for updates.</param>
		/// <returns>true if the cache is clean, or false if it needs to run.</returns>
		internal static bool CheckCache(PathGrid grid, int cell) {
			// If nothing has changed since last time, it is a hit!
			bool hit = IsValid(grid) && (!grid.applyOffset || Grid.XYToCell(grid.rootX + grid.
				widthInCells / 2, grid.rootY + grid.heightInCells / 2) == cell);
			if (FastTrackOptions.Instance.Metrics)
				Metrics.DebugMetrics.PATH_CACHE.Log(hit);
			return hit;
		}

		/// <summary>
		/// Avoid leaking the PathGrids when the game ends.
		/// </summary>
		internal static void Cleanup() {
			PATH_CACHE.Clear();
		}

		/// <summary>
		/// When a PathGrid is destroyed, remove its cached information.
		/// </summary>
		/// <param name="grid">The path prober that was destroyed.</param>
		internal static void Cleanup(PathGrid grid) {
			if (grid != null && PATH_CACHE.TryRemove(grid, out var data))
				POOL.Enqueue(data);
		}

		/// <summary>
		/// When the game is started, reset the path prober caches.
		/// </summary>
		internal static void Init() {
			PATH_CACHE.Clear();
		}
		
		/// <summary>
		/// Sets all Duplicant paths to invalid.
		/// </summary>
		internal static void InvalidateAllDuplicants() {
			var ids = Components.LiveMinionIdentities;
			int n = ids.Count;
			for (int i = 0; i < n; i++) {
				var id = ids[i];
				Navigator nav;
				// navigator is initialized in a Sim1000...
				if (id != null && (nav = id.navigator) != null)
					Cleanup(nav.PathGrid);
			}
		}

		/// <summary>
		/// Invalidates all path caches that intersect the specified region.
		/// </summary>
		/// <param name="minX">The minimum X to invalidate, inclusive.</param>
		/// <param name="maxX">The maximum X to invalidate, inclusive.</param>
		/// <param name="minY">The minimum Y to invalidate, inclusive.</param>
		/// <param name="maxY">The maximum Y to invalidate, inclusive.</param>
		internal static void InvalidateRegion(int minX, int minY, int maxX, int maxY) {
			var toRemove = ListPool<PathGrid, NavGrid>.Allocate();
			foreach (var pair in PATH_CACHE) {
				var key = pair.Key;
				var data = pair.Value;
				lock (data) {
					int dx = data.minX, dy = data.minY;
					// If minX is still -1 (no cells), invalidate as a precaution
					if (dx < 0 || dy < 0 || (minX < data.maxX && maxX > dx &&
							minY < data.maxY && maxY > dy))
						toRemove.Add(key);
				}
			}
			int n = toRemove.Count;
			for (int i = 0; i < n; i++)
				Cleanup(toRemove[i]);
			toRemove.Recycle();
		}

		/// <summary>
		/// Checks to see if the grid's cache is valid.
		/// </summary>
		/// <param name="grid">The path grid to look up.</param>
		/// <returns>true if the cache is valid for this ID, or false otherwise.</returns>
		internal static bool IsValid(PathGrid grid) {
			if (grid == null)
				throw new ArgumentNullException(nameof(grid));
			return PATH_CACHE.TryGetValue(grid, out var data) && now < data.expiration;
		}

		/// <summary>
		/// Sets a grid as valid.
		/// </summary>
		/// <param name="grid">The path grid to look up.</param>
		/// <param name="minX">The minimum X value reachable by the grid.</param>
		/// <param name="maxX">The maximum X value reachable by the grid.</param>
		/// <param name="minY">The minimum Y value reachable by the grid.</param>
		/// <param name="maxY">The maximum Y value reachable by the grid.</param>
		internal static void SetValid(PathGrid grid, int minX, int minY, int maxX, int maxY) {
			if (grid == null)
				throw new ArgumentNullException(nameof(grid));
			// Do not cache "PathFinder.PathGrid" as it is used for disparate queries
			if (grid != PathFinder.PathGrid) {
				// For an atomic get/add operation, an object to insert needs to be available
				// ahead of time, so make one available, try to update, and if it already
				// existed then return the data to the pool
				if (!POOL.TryDequeue(out var headData))
					headData = new CacheData();
				var data = PATH_CACHE.GetOrAdd(grid, headData);
				if (data == headData)
					POOL.Enqueue(headData);
				lock (data) {
					data.expiration = now + INVALIDATE_TIME;
					data.minX = minX;
					data.maxX = maxX;
					data.minY = minY;
					data.maxY = maxY;
				}
			}
		}

		/// <summary>
		/// Updates the current time.
		/// </summary>
		/// <param name="time">The current scaled game time.</param>
		internal static void UpdateTime(double time) {
			now = time;
		}

		/// <summary>
		/// Stores data about a path cache's extends and validity.
		/// </summary>
		private sealed class CacheData {
			internal double expiration;

			internal int minX;
			internal int maxX;
			internal int minY;
			internal int maxY;

			internal CacheData() {
				Reset();
			}

			internal void Reset() {
				minX = -1;
				maxX = -1;
				minY = -1;
				maxY = -1;

				expiration = 0.0;
			}
		}
	}
}
