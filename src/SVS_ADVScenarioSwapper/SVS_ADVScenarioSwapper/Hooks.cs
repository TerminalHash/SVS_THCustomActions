#nullable enable
using System;
using ADV;
using HarmonyLib;

namespace SVS_ADVScenarioSwapper
{
    /// <summary>
    /// One patch: set the ADVLoader tag before ADVLoader's own
    /// <c>OpenData.Load</c> prefix (priority 200) resolves the variant file.
    /// The asset name is deliberately not rewritten, so trigger and refusal
    /// detection still see what the game asked for (<c>a_509</c>,
    /// <c>no_0</c>, ...).
    /// </summary>
    internal static class Hooks
    {
        private static Harmony? _harmony;

        internal static void PatchAll()
        {
            if (_harmony != null) return;
            _harmony = Harmony.CreateAndPatchAll(typeof(Hooks), Plugin.GUID);
            Plugin.Info("[Hooks] Harmony patches installed.");
        }

        internal static void Unpatch()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
        }

        [HarmonyPrefix]
        [HarmonyPriority(300)]
        [HarmonyPatch(typeof(ADV.OpenData), nameof(ADV.OpenData.Load), typeof(string), typeof(string))]
        private static void OpenDataLoadPrefix(string bundle, string asset)
        {
            try { ScenarioSwap.OnOpenDataLoad(bundle, asset); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Scenario swap failed: {ex.Message}"); }
        }
    }
}
