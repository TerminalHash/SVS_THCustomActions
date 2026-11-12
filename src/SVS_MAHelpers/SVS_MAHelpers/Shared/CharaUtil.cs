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

        /// <summary>Param index of the live PC, or -1.</summary>
        internal static int PcIndex()
        {
            try { return SV.GameChara.Player.charasGameParam.Index; }
            catch { return -1; }
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

        /// <summary>
        /// Live talk tasks captured while a custom scenario loads, plus each
        /// task's ordered slot-0/1 pair. An ask binds to this exact set: a later
        /// Talk End only matches when it is one of these task instances or has
        /// the same ordered pair.
        /// </summary>
        internal sealed class TalkIdentity
        {
            public readonly List<SV.Talk.TalkTaskBase> Tasks = new();
            public readonly List<(int A, int B)> Pairs = new();
        }

        /// <summary>
        /// Snapshot of the talk task that owns the loading custom scenario.
        /// The task whose first two slots carry the scenario's NPC and the PC
        /// is preferred, then a task carrying <paramref name="actionNo"/>, then
        /// the newest live task. Null when the task list is unreadable, and
        /// empty when no candidate exists — both fail closed.
        /// </summary>
        internal static TalkIdentity? SnapshotActiveTalkTasks(int actionNo, int npcIdx)
        {
            try
            {
                var tasks = Manager.TalkManager.Instance?.tasks;
                if (tasks == null) return null;
                var snap = new TalkIdentity();
                SV.Talk.TalkTaskBase? last = null;
                SV.Talk.TalkTaskBase? idMatch = null;
                SV.Talk.TalkTaskBase? pairMatch = null;
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    last = t;
                    int no = -1;
                    try { no = t.TaskActionNo; } catch { no = -1; }
                    if (no >= 0 && no == actionNo) idMatch = t;
                    if (npcIdx >= 0 && TaskHasPcAndNpc(t, npcIdx)) pairMatch = t;
                }
                // The scenario's own NPC is the strongest signal; the action id
                // is usually not set on the task, and "newest" alone can bind an
                // unrelated background talk.
                var owner = pairMatch ?? idMatch ?? last;
                if (owner == null) return snap;
                snap.Tasks.Add(owner);
                var pair = OrderedPair(owner);
                if (pair.HasValue) snap.Pairs.Add(pair.Value);
                return snap;
            }
            catch { return null; }
        }

        /// <summary>True when slots 0-1 contain both the PC and the given NPC.</summary>
        internal static bool TaskHasPcAndNpc(SV.Talk.TalkTaskBase? task, int npcIdx)
        {
            try
            {
                var charas = task?.charas;
                if (charas == null || charas.Count < 2 || npcIdx < 0) return false;
                bool npc = false, pc = false;
                for (int i = 0; i < 2; i++)
                {
                    var ai = charas[i];
                    if (ai == null) continue;
                    if (IdxOf(ai) == npcIdx) npc = true;
                    if (IsPc(ai)) pc = true;
                }
                return npc && pc;
            }
            catch { return false; }
        }

        /// <summary>True when slots 0-1 of the talk contain the given character index.</summary>
        internal static bool TaskHasCharaInPair(SV.Talk.TalkTaskBase? task, int idx)
        {
            try
            {
                var charas = task?.charas;
                if (charas == null || charas.Count < 2 || idx < 0) return false;
                for (int i = 0; i < 2; i++)
                {
                    var ai = charas[i];
                    if (ai != null && IdxOf(ai) == idx) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>True when slots 0-1 contain exactly the given PC and NPC indices.</summary>
        internal static bool TaskHasPair(SV.Talk.TalkTaskBase? task, int pcIdx, int npcIdx)
        {
            try
            {
                var charas = task?.charas;
                if (charas == null || charas.Count < 2 || pcIdx < 0 || npcIdx < 0) return false;
                bool pc = false, npc = false;
                for (int i = 0; i < 2; i++)
                {
                    var ai = charas[i];
                    if (ai == null) continue;
                    int idx = IdxOf(ai);
                    if (idx == pcIdx) pc = true;
                    if (idx == npcIdx) npc = true;
                }
                return pc && npc;
            }
            catch { return false; }
        }

        /// <summary>PC/npc indices from a task's slots 0-1, or false when unreadable.</summary>
        internal static bool TryGetTaskPcNpc(SV.Talk.TalkTaskBase? task, out int pcIdx, out int npcIdx)
        {
            pcIdx = -1;
            npcIdx = -1;
            try
            {
                var charas = task?.charas;
                if (charas == null || charas.Count < 2) return false;
                for (int i = 0; i < 2; i++)
                {
                    var ai = charas[i];
                    if (ai == null) continue;
                    int idx = IdxOf(ai);
                    if (idx < 0) continue;
                    if (IsPc(ai)) { if (pcIdx < 0) pcIdx = idx; }
                    else if (npcIdx < 0) npcIdx = idx;
                }
            }
            catch { }
            return pcIdx >= 0 && npcIdx >= 0;
        }

        /// <summary>
        /// Ordered (slot 0, slot 1) param indices of a talk task, or null when
        /// unreadable. The order carries active/passive semantics, so it must
        /// never be treated as a set.
        /// </summary>
        internal static (int A, int B)? OrderedPair(SV.Talk.TalkTaskBase? task)
        {
            try
            {
                var charas = task?.charas;
                if (charas == null || charas.Count < 2) return null;
                int a = charas[0] != null ? IdxOf(charas[0]) : -1;
                int b = charas[1] != null ? IdxOf(charas[1]) : -1;
                if (a < 0 || b < 0) return null;
                return (a, b);
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
