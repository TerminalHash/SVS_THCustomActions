#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Shared character/talk read helpers. Best-effort throughout: unreadable
    /// state yields a sentinel (-1/false/"?"/null), never a throw.
    /// </summary>
    internal static class CharaUtil
    {
        /// <summary>Live roster AI by param index, or null.</summary>
        internal static AI? FindAI(int idx)
        {
            try
            {
                if (idx < 0) return null;
                return SV.GameChara.FindCharaAI(idx);
            }
            catch { return null; }
        }

        /// <summary>Param index of a live AI, or -1.</summary>
        internal static int IdxOf(AI? ai)
        {
            try
            {
                if (ai == null) return -1;
                return ai.charaData.charasGameParam.Index;
            }
            catch { return -1; }
        }

        /// <summary>True when the AI is the PC.</summary>
        internal static bool IsPc(AI? ai)
        {
            try
            {
                var bc = ai?.BehaviourCtrl;
                return bc != null && bc.IsPC;
            }
            catch { return false; }
        }

        /// <summary>Full name of a live AI ("null"/"?" when unreadable).</summary>
        internal static string NameOf(AI? ai)
        {
            try
            {
                if (ai == null) return "null";
                return ai.charaData?.charFile?.Parameter?.fullname ?? "?";
            }
            catch { return "?"; }
        }

        /// <summary>One-line [name idx= pc=] dump of a participant snapshot.</summary>
        internal static string DescribeAll(List<AI>? list)
        {
            if (list == null) return "unknown";
            var parts = new List<string>();
            try
            {
                foreach (var ai in list)
                {
                    if (ai == null) { parts.Add("null"); continue; }
                    parts.Add($"[{NameOf(ai)} idx={IdxOf(ai)} pc={IsPc(ai)}]");
                }
            }
            catch { }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>Param indices of every AI in every live talk (null when unreadable).</summary>
        internal static HashSet<int>? SnapshotActiveTalkCharas()
        {
            try
            {
                var tasks = Manager.TalkManager.Instance?.tasks;
                if (tasks == null) return null;
                var ids = new HashSet<int>();
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    try
                    {
                        var charas = t.charas;
                        if (charas == null) continue;
                        foreach (var ai in charas)
                        {
                            if (ai == null) continue;
                            try { ids.Add(ai.charaData.charasGameParam.Index); } catch { }
                        }
                    }
                    catch { }
                }
                return ids;
            }
            catch { return null; }
        }

        /// <summary>
        /// Resolves a scenario-slot Actor to its live AI. Live reference first,
        /// then shared param index, then roster match by index key and name.
        /// </summary>
        internal static AI? ActorToAI(SaveData.Actor? actor)
        {
            try
            {
                if (actor == null) return null;
                try
                {
                    var ai = SV.GameChara.FindCharaAI(actor);
                    if (ai != null) return ai;
                }
                catch { }
                int idx = -1;
                try { idx = actor.charasGameParam.Index; } catch { }
                if (idx >= 0)
                {
                    try
                    {
                        var ai = SV.GameChara.FindCharaAI(idx);
                        if (ai != null) return ai;
                    }
                    catch { }
                    try
                    {
                        var ai = SV.GameChara.GetAI(idx);
                        if (ai != null) return ai;
                    }
                    catch { }
                }
                // Scenario slots may hold actor copies: match the live roster
                // (index first, then name).
                try
                {
                    string nm = actor.charFile?.Parameter?.fullname ?? "";
                    var charas = Manager.Game.Charas;
                    if (charas != null)
                    {
                        foreach (var kv in charas)
                        {
                            try
                            {
                                if (kv.Value == null) continue;
                                var ai = SV.GameChara.FindCharaAI(kv.Value);
                                if (ai == null) continue;
                                if (idx >= 0 && kv.Key == idx) return ai;
                                if (!string.IsNullOrEmpty(nm))
                                {
                                    string ln = ai.charaData?.charFile?.Parameter?.fullname ?? "";
                                    if (string.Equals(ln, nm, StringComparison.Ordinal))
                                    {
                                        // Duplicate fullnames would misresolve here;
                                        // log the fallback so a wrong pick is traceable.
                                        Plugin.Info($"[Chara] ActorToAI name-fallback: '{nm}' -> idx={idx}.");
                                        return ai;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
            return null;
        }
    }
}
