#nullable enable
using System;
using SV.Chara;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.IgnoreMe
{
    /// <summary>
    /// Vanilla agreement bookkeeping for KeepHimAway (CommandNo 0x4F, dec 79).
    /// <para>
    /// Vanilla SetCommandCount adds the same units to both memories when an
    /// interpersonal talk ends: activeMem.activeCommand.Add(passiveIdx, answer,
    /// commandNo) and passiveMem.passiveCommand.Add(activeIdx, answer, commandNo).
    /// Every Add writes OneTimeTable, DayTable and DeadTable; count is +1 plus
    /// yes/no/ambiguous +1. Recording here IS the vanilla flow.
    /// </para>
    /// <para>
    /// Vanilla has no period scope (DayTable is per-day, OneTimeTable never
    /// resets, DeadTable accumulates) and no sex/group reset. Every record
    /// therefore carries an exact surgical retraction: only our own added units
    /// are subtracted, floored at 0. A whole-entry remove is deliberately not
    /// used — it would wipe the pair's unrelated vanilla history for other
    /// commands sharing the same partner key.
    /// </para>
    /// </summary>
    internal static class VanillaRecord
    {
        internal const int KeepHimAway = 0x4F; // dec 79

        // Units added to one table dict by our Adds, per bucket. Tracked per
        // bucket (not per answer) so re-asks answered differently retract
        // exactly: a later answer never reinterprets older units.
        internal sealed class TableUnits
        {
            public int Count;
            public int Yes;
            public int No;
            public int Ambiguous;
        }

        // Units added per side (3 table dicts each), for exact retraction.
        internal sealed class SideUnits
        {
            public readonly TableUnits[] Tables = { new TableUnits(), new TableUnits(), new TableUnits() };
        }

        // Memory for a chara index: live AI first, else the game chara table
        // (covers NPCs whose AI is momentarily gone). Null when unresolvable.
        private static SaveData.MemoryParameter? ResolveMemory(int idx)
        {
            try
            {
                var ai = FindAI(idx);
                if (ai != null)
                {
                    try { return ai.charaData?.charasGameParam?.memory; }
                    catch { }
                }
            }
            catch { }
            try
            {
                var charas = Manager.Game.Charas;
                if (charas == null) return null;
                foreach (var kv in charas)
                {
                    try
                    {
                        if (kv.Key != idx || kv.Value == null) continue;
                        var ai = SV.GameChara.FindCharaAI(kv.Value);
                        if (ai == null) continue;
                        return ai.charaData?.charasGameParam?.memory;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // Snapshot of our command's info in one table dict (zeros when absent).
        private static void SnapDict(Il2CppSystem.Collections.Generic.Dictionary<int, SaveData.CommandCount>? dict, int key,
            out int count, out int yes, out int no, out int amb)
        {
            count = 0; yes = 0; no = 0; amb = 0;
            try
            {
                if (dict == null) return;
                if (!dict.TryGetValue(key, out var cc) || cc == null) return;
                var info = cc.Get(KeepHimAway);
                if (info == null) return;
                try { count = info.Count; } catch { }
                try { yes = info.Yes; } catch { }
                try { no = info.No; } catch { }
                try { amb = info.Ambiguous; } catch { }
            }
            catch { }
        }

        private static Il2CppSystem.Collections.Generic.Dictionary<int, SaveData.CommandCount>[] DictsOf(SaveData.CommandTable? t)
        {
            if (t == null) return Array.Empty<Il2CppSystem.Collections.Generic.Dictionary<int, SaveData.CommandCount>>();
            try { return new[] { t.DayTable, t.OneTimeTable, t.DeadTable }; }
            catch { return Array.Empty<Il2CppSystem.Collections.Generic.Dictionary<int, SaveData.CommandCount>>(); }
        }

        // Records "npc agreed to KeepHimAway toward pc with the given answer",
        // exactly as SetCommandCount would for (active=npc, passive=pc,
        // commandNo=0x4F, judgeAnswer=answer).
        internal static void Record(int npcIdx, int pcIdx, int answer, out SideUnits npcUnits, out SideUnits pcUnits)
        {
            npcUnits = new SideUnits();
            pcUnits = new SideUnits();
            try
            {
                var npcMem = ResolveMemory(npcIdx);
                var pcMem = ResolveMemory(pcIdx);
                if (npcMem == null || pcMem == null)
                {
                    Plugin.Log.LogWarning($"[IgnoreMe] Record skipped: memory unresolvable (npc={npcIdx} pc={pcIdx}).");
                    return;
                }
                SaveData.CommandTable? npcActive, pcPassive;
                try { npcActive = npcMem.activeCommand; } catch { npcActive = null; }
                try { pcPassive = pcMem.passiveCommand; } catch { pcPassive = null; }
                if (npcActive == null || pcPassive == null)
                {
                    Plugin.Log.LogWarning($"[IgnoreMe] Record skipped: command tables unresolvable (npc={npcIdx} pc={pcIdx}).");
                    return;
                }
                var nDicts = DictsOf(npcActive);
                var pDicts = DictsOf(pcPassive);
                if (nDicts.Length != 3 || pDicts.Length != 3)
                {
                    Plugin.Log.LogWarning($"[IgnoreMe] Record skipped: table dicts unreadable (npc={npcIdx} pc={pcIdx}).");
                    return;
                }
                int[,] n0 = new int[3, 4], p0 = new int[3, 4];
                for (int i = 0; i < 3; i++)
                {
                    SnapDict(nDicts[i], pcIdx, out n0[i, 0], out n0[i, 1], out n0[i, 2], out n0[i, 3]);
                    SnapDict(pDicts[i], npcIdx, out p0[i, 0], out p0[i, 1], out p0[i, 2], out p0[i, 3]);
                }
                try { npcActive.Add(pcIdx, answer, KeepHimAway); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[IgnoreMe] Record npc-side Add failed: {ex.Message}");
                    return;
                }
                try { pcPassive.Add(npcIdx, answer, KeepHimAway); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] Record pc-side Add failed: {ex.Message}"); }
                for (int i = 0; i < 3; i++)
                {
                    SnapDict(nDicts[i], pcIdx, out int nC1, out int nY1, out int nN1, out int nA1);
                    SnapDict(pDicts[i], npcIdx, out int pC1, out int pY1, out int pN1, out int pA1);
                    npcUnits.Tables[i].Count = Math.Max(0, nC1 - n0[i, 0]);
                    npcUnits.Tables[i].Yes = Math.Max(0, nY1 - n0[i, 1]);
                    npcUnits.Tables[i].No = Math.Max(0, nN1 - n0[i, 2]);
                    npcUnits.Tables[i].Ambiguous = Math.Max(0, nA1 - n0[i, 3]);
                    pcUnits.Tables[i].Count = Math.Max(0, pC1 - p0[i, 0]);
                    pcUnits.Tables[i].Yes = Math.Max(0, pY1 - p0[i, 1]);
                    pcUnits.Tables[i].No = Math.Max(0, pN1 - p0[i, 2]);
                    pcUnits.Tables[i].Ambiguous = Math.Max(0, pA1 - p0[i, 3]);
                }
                Plugin.Info($"[IgnoreMe] Recorded KeepHimAway npc={npcIdx} pc={pcIdx} answer={answer} " +
                    $"(npc-side +{npcUnits.Tables[0].Count}/+{npcUnits.Tables[1].Count}/+{npcUnits.Tables[2].Count}, " +
                    $"pc-side +{pcUnits.Tables[0].Count}/+{pcUnits.Tables[1].Count}/+{pcUnits.Tables[2].Count}).");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] Record failed: {ex.Message}"); }
        }

        // Retracts exactly what Record added (floored at 0): per table dict,
        // count and every answer bucket minus their recorded units. Returns
        // false only when memory/tables are unresolvable, so the caller can
        // keep the bookkeeping and retry instead of losing the units.
        internal static bool Retract(int npcIdx, int pcIdx, SideUnits npcUnits, SideUnits pcUnits)
        {
            try
            {
                var npcMem = ResolveMemory(npcIdx);
                var pcMem = ResolveMemory(pcIdx);
                if (npcMem == null || pcMem == null)
                {
                    Plugin.Info($"[IgnoreMe] Retract skipped: memory unresolvable (npc={npcIdx} pc={pcIdx}).");
                    return false;
                }
                SaveData.CommandTable? npcActive, pcPassive;
                try { npcActive = npcMem.activeCommand; } catch { npcActive = null; }
                try { pcPassive = pcMem.passiveCommand; } catch { pcPassive = null; }
                if (npcActive == null || pcPassive == null)
                {
                    Plugin.Info($"[IgnoreMe] Retract skipped: command tables unresolvable (npc={npcIdx} pc={pcIdx}).");
                    return false;
                }
                int touched = RetractSide(npcActive, pcIdx, npcUnits)
                    + RetractSide(pcPassive, npcIdx, pcUnits);
                if (touched > 0)
                    Plugin.Info($"[IgnoreMe] Retracted KeepHimAway npc={npcIdx} pc={pcIdx} ({touched} rows).");
                else
                    Plugin.Info($"[IgnoreMe] Retract npc={npcIdx} pc={pcIdx}: nothing outstanding to remove.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[IgnoreMe] Retract failed: {ex.Message}");
                return false;
            }
        }

        // Returns how many info rows were actually decremented.
        private static int RetractSide(SaveData.CommandTable table, int key, SideUnits units)
        {
            int touched = 0;
            try
            {
                var dicts = DictsOf(table);
                for (int i = 0; i < dicts.Length && i < units.Tables.Length; i++)
                {
                    var u = units.Tables[i];
                    if (u.Count <= 0 && u.Yes <= 0 && u.No <= 0 && u.Ambiguous <= 0) continue;
                    try
                    {
                        if (!dicts[i].TryGetValue(key, out var cc) || cc == null) continue;
                        var info = cc.Get(KeepHimAway);
                        if (info == null) continue;
                        try { if (u.Count > 0) info.Count = Math.Max(0, info.Count - u.Count); } catch { }
                        try { if (u.Yes > 0) info.Yes = Math.Max(0, info.Yes - u.Yes); } catch { }
                        try { if (u.No > 0) info.No = Math.Max(0, info.No - u.No); } catch { }
                        try { if (u.Ambiguous > 0) info.Ambiguous = Math.Max(0, info.Ambiguous - u.Ambiguous); } catch { }
                        touched++;
                    }
                    catch { }
                }
            }
            catch { }
            return touched;
        }
    }
}
