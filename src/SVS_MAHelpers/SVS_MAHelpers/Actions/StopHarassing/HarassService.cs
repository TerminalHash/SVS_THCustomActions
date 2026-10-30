#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.StopHarassing
{
    /// <summary>
    /// "Stop harassing…" (action 505), simplified logic: after the ask talk
    /// ends, the blackmail state for the picked pair is erased directly — the
    /// victim leaves the blackmailer's <c>memory.activeWeaks</c>, the
    /// blackmailer leaves the victim's <c>memory.passiveWeaks</c>, and the
    /// <c>LOG_WEAKNESSES_FLAG</c> (0x55) logs naming the other side are dropped
    /// from both memories. The list halves are exactly what vanilla
    /// EliminateWeaknesses (82) and SolveMyWeaknesses (9) clear on success; the
    /// logs go too because the thinker keeps keying harassment off them.
    /// A refused ask (no_x/n_x inside the talk) removes nothing.
    /// </summary>
    internal static class HarassService
    {
        private static readonly SaveData.LogInfo.LogType WeaknessLogType =
            SaveData.LogInfo.LogType.LOG_WEAKNESSES_FLAG;
        private static readonly SaveData.LogInfo.LogCheckTime AllTimeCheck =
            SaveData.LogInfo.LogCheckTime.LOG_IS_SOMETIME;

        internal static void Schedule(List<AI>? participants, string reason, AskState ask)
        {
            try
            {
                List<AI>? copy = participants != null ? new List<AI>(participants) : null;
                // Capture this action's own outcome before queuing: TriggerService
                // settles the ask as soon as the handler returns.
                bool refused = ask.TakeRefused();
                SaveData.Actor? picked = ask.Pick;
                Plugin.Info($"[StopHarass] Trigger queued (reason={reason}, participants={(copy == null ? "unknown" : copy.Count.ToString())}, refused={refused}, pick={(picked != null)})");
                TriggerService.RunOnMainThread(() =>
                {
                    try { DoApply(copy, reason, refused, picked); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[StopHarass] DoApply failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[StopHarass] Schedule failed: {ex.Message}"); }
        }

        private static void DoApply(List<AI>? participants, string reason, bool refused, SaveData.Actor? picked)
        {
            if (!TriggerService.TryGetPairTargets(participants, out var pc, out var npcTargets) || pc == null || npcTargets.Count == 0)
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): {DescribeAll(participants)}.");
                return;
            }
            var asker = npcTargets[0];
            int askerIdx = IdxOf(asker);
            if (askerIdx < 0) return;
            Plugin.Info($"[StopHarass] Participants: {DescribeAll(participants)}");

            // Refused ask: a NO-reply scenario played inside THIS talk.
            // (Deliberately no judgeAnswer gate: the 505 judge always yields its
            // default Rate 0 / Answer 1, so gating on it would abort every
            // removal; the in-talk NO-scenario is the real refusal signal.)
            if (refused)
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): refused (NO scenario played).");
                return;
            }

            AI? victim = ResolveVictim(askerIdx, picked);
            if (victim == null)
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): no blackmailed character was picked.");
                return;
            }
            int victimIdx = IdxOf(victim);
            if (victimIdx < 0 || victimIdx == askerIdx)
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): bad victim idx={victimIdx}.");
                return;
            }
            if (IsPc(victim))
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): the victim cannot be the PC.");
                return;
            }
            // The pick must actually be blackmailed by the asked NPC (stale
            // picks die here, never in the picker).
            if (!IsBlackmailedBy(victimIdx, askerIdx))
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): [{NameOf(victim)}] is not blackmailed by [{NameOf(asker)}].");
                return;
            }
            // Strictly pair-scoped: only the asked NPC leaves the victim's
            // blackmailers list, and only the picked victim leaves the asked
            // NPC's blackmailed list.
            var holders = AllHolders(victimIdx);
            Plugin.Info($"[StopHarass] [{NameOf(asker)}] -> [{NameOf(victim)}]: holders=[{string.Join(",", holders)}] (asked={askerIdx}, {reason}).");
            int links = 0, logs = 0;
            if (DropWeakEntry(victim, false, askerIdx)) links++;
            if (DropWeakEntry(asker, true, victimIdx)) links++;
            logs += DropWeaknessLogs(victim, askerIdx);
            logs += DropWeaknessLogs(asker, victimIdx);
            bool linked = IsBlackmailedBy(victimIdx, askerIdx);
            int logsLeft = CountWeaknessLogs(victim, askerIdx) + CountWeaknessLogs(asker, victimIdx);
            if (!linked && logsLeft == 0)
            {
                // Same recompute every vanilla talk tail runs (both directions):
                // derived relationship state refreshes now instead of lagging
                // until the pair's next dialogue.
                RecalcRelationship(asker, victim);
                Plugin.Info($"[StopHarass] [{NameOf(asker)}] stopped harassing [{NameOf(victim)}] (cleared {links} link(s), {logs} log(s), relationship recomputed, {reason}).");
            }
            else
                Plugin.Log.LogWarning($"[StopHarass] [{NameOf(asker)}] -> [{NameOf(victim)}]: weakness remains (links={linked}, logs left={logsLeft}, {reason}).");
        }

        // Recomputes the pair's derived relationship state now (what vanilla
        // talk tails do via RelationshipManager.Calc both directions).
        private static void RecalcRelationship(AI asker, AI victim)
        {
            try
            {
                SaveData.Actor? a = null, v = null;
                try { a = asker.charaData; } catch { }
                try { v = victim.charaData; } catch { }
                if (a == null || v == null) return;
                try { SV.RelationshipManager.Instance.Calc(a, v); }
                catch (Exception ex) { Plugin.Info($"[StopHarass] Relationship Calc failed: {ex.Message}"); }
                try { SV.RelationshipManager.Instance.Calc(v, a); }
                catch (Exception ex) { Plugin.Info($"[StopHarass] Relationship Calc failed: {ex.Message}"); }
                Plugin.Info("[StopHarass] Relationship recomputed both directions.");
            }
            catch { }
        }

        // Every index currently holding the victim (her full passiveWeaks).
        internal static HashSet<int> AllHolders(int victimIdx)
        {
            var set = new HashSet<int>();
            try
            {
                var victimAI = FindAI(victimIdx);
                var pw = victimAI?.charaData?.charasGameParam?.memory?.passiveWeaks;
                if (pw != null)
                {
                    foreach (var h in pw)
                    {
                        try { if (h >= 0) set.Add(h); } catch { }
                    }
                }
            }
            catch { }
            return set;
        }

        // Drops idx from ai's activeWeaks (fromActive) or passiveWeaks.
        // True when the entry was present and is verified gone.
        private static bool DropWeakEntry(AI? ai, bool fromActive, int idx)
        {
            try
            {
                var mem = ai?.charaData?.charasGameParam?.memory;
                if (mem == null || idx < 0) return false;
                var list = fromActive ? mem.activeWeaks : mem.passiveWeaks;
                if (list == null) return false;
                bool had = false;
                try { had = list.Contains(idx); } catch { }
                if (!had) return false;
                try { list.Remove(idx); } catch (Exception ex) { Plugin.Info($"[StopHarass] weak-list remove failed: {ex.Message}"); }
                try { if (!list.Contains(idx)) return true; } catch { }
            }
            catch { }
            return false;
        }

        // Drops ai's LOG_WEAKNESSES_FLAG logs naming otherIdx.
        // Returns the verified-gone count.
        private static int DropWeaknessLogs(AI? ai, int otherIdx)
        {
            int cleared = 0;
            try
            {
                int before = CountWeaknessLogs(ai, otherIdx);
                if (before == 0) return 0;
                try { ai?.charaData?.charasGameParam?.memory?.RemoveLogWithChara(WeaknessLogType, otherIdx); }
                catch (Exception ex) { Plugin.Info($"[StopHarass] weakness-log remove failed: {ex.Message}"); }
                int after = CountWeaknessLogs(ai, otherIdx);
                if (before - after > 0) cleared = before - after;
            }
            catch { }
            return cleared;
        }

        // How many LOG_WEAKNESSES_FLAG logs on ai's memory name otherIdx.
        private static int CountWeaknessLogs(AI? ai, int otherIdx)
        {
            int n = 0;
            try
            {
                if (ai == null || otherIdx < 0) return 0;
                var mem = ai.charaData?.charasGameParam?.memory;
                if (mem == null) return 0;
                var logs = mem.GetLogs(WeaknessLogType, AllTimeCheck);
                if (logs == null) return 0;
                foreach (var l in logs)
                {
                    try
                    {
                        var charas = l?.charas;
                        if (charas == null) continue;
                        if (charas.Contains(otherIdx)) n++;
                    }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        // Victim resolution: the captured picker Actor only. It must actually be
        // blackmailed by the asker (checked by the caller) — a stale or missing
        // pick resolves to nothing, never to a guess.
        private static AI? ResolveVictim(int askerIdx, SaveData.Actor? picked)
        {
            AI? pickAI = null;
            try { pickAI = ActorToAI(picked); }
            catch (Exception ex) { Plugin.Info($"[StopHarass] Pick resolve failed: {ex.Message}"); }
            if (pickAI == null) return null;
            try
            {
                if (IsPc(pickAI)) return null;
                int idx = IdxOf(pickAI);
                if (idx < 0 || idx == askerIdx) return null;
                return pickAI;
            }
            catch { return null; }
        }

        // Blackmail truth: the asker's activeWeaks holds the victim, or the
        // victim's passiveWeaks holds the asker (either side proves the link).
        internal static bool IsBlackmailedBy(int victimIdx, int askerIdx)
        {
            try
            {
                var askerAI = FindAI(askerIdx);
                var aw = askerAI?.charaData?.charasGameParam?.memory?.activeWeaks;
                if (aw != null)
                {
                    bool hit = false;
                    try { hit = aw.Contains(victimIdx); } catch { }
                    if (hit) return true;
                }
            }
            catch { }
            try
            {
                var victimAI = FindAI(victimIdx);
                var pw = victimAI?.charaData?.charasGameParam?.memory?.passiveWeaks;
                if (pw != null)
                {
                    bool hit = false;
                    try { hit = pw.Contains(askerIdx); } catch { }
                    if (hit) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
