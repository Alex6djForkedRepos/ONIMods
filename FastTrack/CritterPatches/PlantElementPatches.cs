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

using HarmonyLib;
using Klei.AI;
using System.Collections.Generic;
using static STRINGS.MISC;

namespace PeterHan.FastTrack.CritterPatches {
	/// <summary>
	/// Groups patches used to optimize plant fertilization.
	/// </summary>
	public static class FertilizerMonitorPatches {
		/// <summary>
		/// Caches the fertilizer usage attribute as it is referenced by each plant.
		/// </summary>
		private static Attribute fertilizerUsage;

		internal static void Init() {
			fertilizerUsage = Db.Get().PlantAttributes.FertilizerUsageMod;
		}

		/// <summary>
		/// Gets the available fertilizers or irrigants.
		/// </summary>
		/// <param name="source">The source location for the materials.</param>
		/// <param name="fertilizer">The location where the fertilizers will be placed.</param>
		internal static void GetFertilizers(Storage source, IList<KPrefabID> fertilizer) {
			var items = source.items;
			int n = items.Count;
			for (int i = 0; i < n; i++)
				// No guarantee sadly that the Element of PrimaryElement has the tag for
				// which FertilizationMonitor is looking
				if (items[i].TryGetComponent(out KPrefabID kpid))
					fertilizer.Add(kpid);
		}

		/// <summary>
		/// Gets the current fertilizer usage modifier.
		/// </summary>
		/// <param name="plant">The target plant.</param>
		/// <returns>The current fertilizer usage multiplier (mutations etc).</returns>
		internal static float GetFertilizerUsage(UnityEngine.GameObject plant) {
			return plant.GetAttributes().Get(fertilizerUsage).GetTotalValue();
		}

		/// <summary>
		/// Gets the available mass for a given fertilizer requirement.
		/// </summary>
		/// <param name="fertilizer">The available fertilizers.</param>
		/// <param name="targetTag">The type of fertilizer desired.</param>
		/// <param name="wrongTag">The type of item that is considered the wrong fertilizer.</param>
		/// <param name="valid">A list of tags for valid fertilizers.</param>
		/// <param name="wrong">Whether the wrong type of fertilizer has been found.</param>
		/// <returns>The mass available of this fertilizer type, or 0.0f if no match is found.</returns>
		internal static float GetMass(IList<KPrefabID> fertilizer, Tag targetTag, Tag wrongTag,
				List<Tag> valid, ref bool wrong) {
			int n = fertilizer.Count;
			float mass = 0.0f;
			bool hasInvalid = wrong;
			for (int i = 0; i < n; i++) {
				var item = fertilizer[i];
				if (item.HasTag(targetTag)) {
					// Can theoretically double-count but this occurs in base game too
					if (item.TryGetComponent(out PrimaryElement pe))
						mass += pe.Mass;
				} else if (!hasInvalid && item.HasTag(wrongTag) && !item.HasAnyTags(valid))
					// Make sure the possibly wrong element does not have any of the right tags
					wrong = hasInvalid = true;
			}
			return mass;
		}

		/// <summary>
		/// Creates a list of all valid consumption tags.
		/// </summary>
		/// <param name="consumed">The types of fertilizer that can be consumed.</param>
		/// <param name="tags">The location where the tags will be stored.</param>
		internal static void ListTags(PlantElementAbsorber.ConsumeInfo[] consumed,
				ICollection<Tag> tags) {
			int n = consumed.Length;
			for (int i = 0; i < n; i++) {
				ref var consumeInfo = ref consumed[i];
				tags.Add(consumeInfo.tag);
			}
		}

		/// <summary>
		/// Applied to IrrigationMonitor.Instance to reduce the number of GetComponent calls
		/// every frame.
		/// </summary>
		[HarmonyPatch(typeof(IrrigationMonitor.Instance), nameof(IrrigationMonitor.Instance.
			UpdateIrrigation))]
		internal static class UpdateIrrigation_Patch {
			internal static bool Prepare() => FastTrackOptions.Instance.FlattenAverages;

			/// <summary>
			/// Applied before UpdateIrrigation runs.
			/// </summary>
			[HarmonyPriority(Priority.Low)]
			internal static bool Prefix(IrrigationMonitor.Instance __instance, float dt) {
				var consumed = __instance.def.consumedElements;
				var storage = __instance.storage;
				var wrongTag = __instance.def.wrongIrrigationTestTag;
				var sm = __instance.sm;
				bool correct = false;
				bool wrong = false;
				bool canRecover = false;
				if (consumed != null && storage != null) {
					float modifier = GetFertilizerUsage(__instance.gameObject) * dt;
					int n = consumed.Length;
					var irrigant = ListPool<KPrefabID, IrrigationMonitor>.Allocate();
					var valid = ListPool<Tag, IrrigationMonitor>.Allocate();
					GetFertilizers(storage, irrigant);
					ListTags(consumed, valid);
					for (int i = 0; i < n; i++) {
						ref var consumeInfo = ref consumed[i];
						float mass = GetMass(irrigant, consumeInfo.tag, wrongTag, valid,
							ref wrong), target = consumeInfo.massConsumptionRate * modifier;
						if (mass > __instance.total_available_mass)
							__instance.total_available_mass = mass;
						if (mass >= target) {
							correct = true;
							// Could not find this constant in the game code
							canRecover = mass >= target * 30.0f;
							break;
						}
					}
					valid.Recycle();
					irrigant.Recycle();
				}
				sm.hasCorrectLiquid.Set(correct, __instance);
				sm.hasIncorrectLiquid.Set(wrong, __instance);
				sm.enoughCorrectLiquidToRecover.Set(canRecover && correct, __instance);
				return false;
			}
		}
	}
}
