#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ADV;
using UnityEngine;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Trait/situation dialogue variants for scenario assets. A variant is an
    /// ADVLoader tag file beside the base scenario
    /// (<c>chara_bmail100_masochist.json</c>); the swap sets the tag flag
    /// instead of rewriting the asset name, so trigger/refusal detection still
    /// sees <c>a_509</c> / <c>no_0</c>. ADVLoader clears flags after each load,
    /// hence the high-priority <c>OpenData.Load</c> prefix. It is optional:
    /// reflection keeps it a soft dependency and a missing install no-ops.
    ///
    /// Trait suffixes are single lowercase tags: ADVLoader treats every
    /// '_'-separated word as its own tag, so multi-word names are collapsed
    /// (<c>_bwguys</c>, <c>_hotheaded</c>). <c>_inlove</c>, <c>_male</c>,
    /// <c>_female</c> cover the rest. The numeric <c>_trait{id}</c> form is
    /// tried only with the test setting.
    /// Candidates must exist in the folder ADVLoader will search; several are
    /// picked at random, and only <c>no_0</c> is swapped for NO answers.
    /// </summary>
    internal static class ScenarioSwap
    {
        // Hardcoded suffixes, one tag each. The four doc examples are
        // 35 Masochist, 36 Evil, 28 Selfish and 31 Schemer; the rest are here
        // so a content pack can add a variant for any vanilla trait without the
        // _traitxxx setting. Multi-word names are abbreviated, never joined
        // with '_' (ADVLoader would read each word as a separate tag).
        private static readonly Dictionary<int, string> TraitSuffix = new()
        {
            [0] = "_easygoing",
            [1] = "_affable",
            [2] = "_bwguys",
            [3] = "_bwgirls",
            [4] = "_charming",
            [5] = "_chivalrous",
            [6] = "_trendy",
            [7] = "_obedient",
            [8] = "_positive",
            [9] = "_shy",
            [10] = "_jealous",
            [11] = "_melancholy",
            [12] = "_perverted",
            [13] = "_serious",
            [14] = "_calm",
            [15] = "_nervous",
            [16] = "_impulsive",
            [17] = "_absentminded",
            [18] = "_hotheaded",
            [19] = "_carnivorous",
            [20] = "_herbivorous",
            [21] = "_meddlesome",
            [22] = "_leader",
            [23] = "_musclehead",
            [24] = "_chatty",
            [25] = "_gluttonous",
            [26] = "_diligent",
            [27] = "_romantic",
            [28] = "_selfish",
            [29] = "_singleminded",
            [30] = "_indecisive",
            [31] = "_schemer",
            [32] = "_inoffensive",
            [33] = "_workaholic",
            [34] = "_wild",
            [35] = "_masochist",
            [36] = "_evil",
            [37] = "_thickheaded",
            [38] = "_blind",
            [39] = "_lucky",
        };

        // ADVLoader uses these relative roots; AddADVList can override them.
        private static readonly Dictionary<string, string> DefaultFolders = new()
        {
            ["chara"] = "abdata/mods/ADVLoader/Scenario/Chara/",
            ["common"] = "abdata/mods/ADVLoader/Scenario/Common/",
            ["other"] = "abdata/mods/ADVLoader/Scenario/Other/",
        };

        private static MethodInfo? _setFlag;
        private static MethodInfo? _getAssetType;
        private static MethodInfo? _getFolders;

        /// <summary>
        /// Called from the high-priority <c>ADV.OpenData.Load</c> prefix, i.e.
        /// after the scenario has selected its heroine but before ADVLoader
        /// resolves the tag file.
        /// </summary>
        internal static void OnOpenDataLoad(string bundle, string asset)
        {
            try
            {
                if (string.IsNullOrEmpty(asset) || !NoAnswerAllowed(asset)) return;

                SaveData.Actor? heroine = CurrentHeroine();
                if (heroine == null) return;

                var variants = ExistingVariants(heroine, bundle, asset);
                if (variants == null || variants.Count == 0) return;

                var candidates = BuildCandidates(heroine, asset, variants);
                if (candidates.Count == 0) return;

                string suffix = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                string flag = suffix.TrimStart('_').Replace("_", "");
                if (!SetFlag(flag, true)) return;

                Plugin.Info($"[Swap] '{asset}' + '{suffix}' ({TraitIds(heroine)}).");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Swap] Scenario swap failed: {ex.Message}"); }
        }

        // The action scenarios open no_0 on NO返答 and n_0 on 難聴 (see
        // SVS_Misc/EventFiles/Actions/a_506.json). The doc allows swapping only
        // no_0, so no_1+, n_* and exact "no"/"n" keep the base scenario.
        private static bool NoAnswerAllowed(string asset)
        {
            if (asset.StartsWith("no_", StringComparison.OrdinalIgnoreCase))
                return string.Equals(asset, "no_0", StringComparison.OrdinalIgnoreCase);
            return !string.Equals(asset, "no", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(asset, "n", StringComparison.OrdinalIgnoreCase)
                && !asset.StartsWith("n_", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> BuildCandidates(SaveData.Actor heroine, string asset,
            HashSet<string> variants)
        {
            var list = new List<string>();

            try
            {
                var answer = heroine.gameParameter?.individuality?.answer;
                if (answer != null)
                {
                    for (int i = 0; i < answer.Length; i++)
                    {
                        int id = answer[i];
                        if (id < 0) continue;
                        if (TraitSuffix.TryGetValue(id, out string? hardcoded))
                            AddIfExists(list, asset, hardcoded, variants);
                        if (Plugin.UseTraitSuffix != null && Plugin.UseTraitSuffix.Value)
                            AddIfExists(list, asset, "_trait" + id, variants);
                    }
                }
            }
            catch { }

            int sex = -1;
            try { sex = heroine.parameter.sex; } catch { }
            if (sex == 0) AddIfExists(list, asset, "_male", variants);
            else if (sex == 1) AddIfExists(list, asset, "_female", variants);

            if (BothLovers(heroine)) AddIfExists(list, asset, "_inlove", variants);

            return list;
        }

        private static void AddIfExists(List<string> list, string asset, string suffix,
            HashSet<string> variants)
        {
            if (string.IsNullOrEmpty(suffix) || list.Contains(suffix)) return;
            if (variants.Contains(asset + suffix + ".json")) list.Add(suffix);
        }

        // Mirrors ADVLoader.SideLoadADV: the scenario type selects the root,
        // Chara uses the heroine's own personality folders, and only the first
        // folder containing asset*.json is consulted (ADVLoader breaks there).
        private static HashSet<string>? ExistingVariants(SaveData.Actor heroine, string bundle,
            string asset)
        {
            try
            {
                if (!ResolveAdvLoader()) return null;
                string? key = FolderKey(asset, bundle);
                if (key == null) return null;

                string rel = "";
                try { rel = _getFolders?.Invoke(null, new object[] { key }) as string ?? ""; } catch { }
                if (string.IsNullOrEmpty(rel)) rel = DefaultFolders[key];

                string root = Path.Combine(BepInEx.Paths.GameRootPath, rel);
                if (!Directory.Exists(root)) return null;

                string[] dirs;
                if (key == "chara")
                {
                    int personality;
                    try { personality = heroine.personality; } catch { return null; }
                    if (personality < 0) return null;
                    dirs = Directory.GetDirectories(root, personality + "_*", SearchOption.TopDirectoryOnly);
                }
                else dirs = new[] { root };

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in dirs)
                {
                    string[] files;
                    try { files = Directory.GetFiles(dir, asset + "*.json", SearchOption.AllDirectories); }
                    catch { continue; }
                    if (files.Length == 0) continue;
                    foreach (var file in files) names.Add(Path.GetFileName(file));
                    break; // ADVLoader stops at the first folder with a match
                }
                return names;
            }
            catch { return null; }
        }

        private static string? FolderKey(string asset, string bundle)
        {
            try
            {
                object? type = _getAssetType?.Invoke(null, new object[] { asset, bundle });
                if (type == null) return null;
                return type.ToString() switch
                {
                    "Chara" => "chara",
                    "Common" => "common",
                    _ => "other",
                };
            }
            catch { return null; }
        }

        private static bool BothLovers(SaveData.Actor heroine)
        {
            try
            {
                var pc = SV.GameChara.Player;
                if (pc == null) return false;
                int npcIdx = heroine.charasGameParam.Index;
                int pcIdx = pc.charasGameParam.Index;
                if (npcIdx < 0 || pcIdx < 0) return false;
                bool npcLovesPc = heroine.charasGameParam.memory.IsLoversContainsID(pcIdx);
                bool pcLovesNpc = pc.charasGameParam.memory.IsLoversContainsID(npcIdx);
                return npcLovesPc && pcLovesNpc;
            }
            catch { return false; }
        }

        private static SaveData.Actor? CurrentHeroine()
        {
            try
            {
                var manager = ADVManager.Instance;
                if (manager == null) return null;
                var scene = manager.gameObject.GetComponentInChildren<ADVScene>();
                if (scene == null) return null;
                return scene._scenario?.CurrentHeroine;
            }
            catch { return null; }
        }

        private static bool ResolveAdvLoader()
        {
            if (_getAssetType != null) return true;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("SVS_ADVLoader.ADVLoader", false);
                    if (type == null) continue;
                    _getAssetType = type.GetMethod("GetAssetType",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(string) }, null);
                    if (_getAssetType == null) continue;
                    var param = assembly.GetType("SVS_ADVLoader.ADVLoaderParam", false);
                    _getFolders = param?.GetMethod("GetADVFolders",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string) }, null);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool SetFlag(string flag, bool on)
        {
            try
            {
                var method = ResolveSetFlag();
                if (method == null) return false;
                method.Invoke(null, new object[] { flag, on });
                return true;
            }
            catch { return false; }
        }

        private static MethodInfo? ResolveSetFlag()
        {
            if (_setFlag != null) return _setFlag;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("SVS_ADVLoader.ADVFlags", false);
                    if (type == null) continue;
                    _setFlag = type.GetMethod("SetADVFlag",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(bool) }, null);
                    if (_setFlag != null) break;
                }
            }
            catch { }
            return _setFlag;
        }

        private static string TraitIds(SaveData.Actor heroine)
        {
            try
            {
                var answer = heroine.gameParameter?.individuality?.answer;
                if (answer == null) return "traits=?";
                var ids = new List<string>();
                for (int i = 0; i < answer.Length; i++)
                    if (answer[i] >= 0) ids.Add(answer[i].ToString());
                return "traits=" + string.Join(",", ids.ToArray());
            }
            catch { return "traits=?"; }
        }
    }
}
