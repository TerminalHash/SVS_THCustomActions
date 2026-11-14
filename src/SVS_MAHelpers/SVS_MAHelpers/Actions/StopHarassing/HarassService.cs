#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;
using SVS_ADVScenarioSwapper;
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

        // "For all cases": one shared trait judge. Evil refuses almost always,
        // Selfish refuses less often, and Schemer keeps YES but the trigger
        // skips the removal. Chances are roll-based, not the vanilla rate.
        private const int TraitSelfish = 28;
        private const int TraitSchemer = 31;
        private const int TraitEvil = 36;
        private const int EvilNoChance = 95;
        private const int SelfishNoChance = 40;
        private const int SchemerFakeChance = 60;

        // Set by the judge at action-menu time and consumed by the 505 trigger.
        // Only one custom action is judged for one asked NPC at a time.
        private static int _fakeYesIdx = -1;

        /// <summary>
        /// Own 505 judge, called from the BaseAnswer.Judge postfix before CGB.
        /// It ignores the vanilla rate: the trait answer is decided here.
        /// </summary>
        internal static void AdjustJudge(int commandID,
            SV.YesNoJudgeManager.AnswerInfo? ansInfo, SV.YesNoJudgeManager.YesNoInfo? ynInfo)
        {
            if (commandID != ActionVars.HarassActionNo || ansInfo == null) return;
            _fakeYesIdx = -1;
            try
            {
                var asked = ynInfo?.passive;
                var traits = asked?.gameParameter?.individuality?.answer;
                if (traits == null) return;
                int npcIdx = asked!.charasGameParam?.Index ?? -1;
                if (npcIdx < 0) return;

                if (HasTrait(traits, TraitEvil))
                {
                    bool forced = Roll(EvilNoChance);
                    if (forced) ForceNo(ansInfo);
                    FakeAnswer.Set(npcIdx, false, forced);
                    Plugin.Info($"[StopHarass] Judge: Evil NPC idx={npcIdx} -> {(forced ? "NO" : "vanilla")}.");
                }
                else if (HasTrait(traits, TraitSchemer))
                {
                    bool fake = Roll(SchemerFakeChance);
                    if (fake) _fakeYesIdx = npcIdx;
                    FakeAnswer.Set(npcIdx, fake, false);
                    Plugin.Info($"[StopHarass] Judge: Schemer NPC idx={npcIdx} -> {(fake ? "fake YES" : "vanilla")}.");
                }
                else if (HasTrait(traits, TraitSelfish))
                {
                    bool forced = Roll(SelfishNoChance);
                    if (forced) ForceNo(ansInfo);
                    FakeAnswer.Set(npcIdx, false, forced);
                    Plugin.Info($"[StopHarass] Judge: Selfish NPC idx={npcIdx} -> {(forced ? "NO" : "vanilla")}.");
                }
            }
            catch (Exception ex) { Plugin.Info($"[StopHarass] Judge failed: {ex.Message}"); }
        }

        // YesNoJudgeManager.Judge turns ans back into YES for a PC with
        // additional function 0x16 when rate > 0, so a forced NO zeroes it.
        private static void ForceNo(SV.YesNoJudgeManager.AnswerInfo ansInfo)
        {
            if (ansInfo.ans == 2) return; // never replace 難聴
            ansInfo.ans = 1;
            if (ansInfo.rate > 0f) ansInfo.rate = 0f;
        }

        private static bool Roll(int chance) => UnityEngine.Random.Range(0, 100) < chance;

        private static bool HasTrait(int[] traits, int id)
        {
            for (int i = 0; i < traits.Length; i++)
                if (traits[i] == id) return true;
            return false;
        }

        // True once for the asked NPC whose Schemer judge chose a fake YES.
        private static bool TakeFakeYes(int npcIdx)
        {
            if (_fakeYesIdx != npcIdx) return false;
            _fakeYesIdx = -1;
            return true;
        }

        internal static void Reset() => _fakeYesIdx = -1;

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
            bool fakeYes = TakeFakeYes(askerIdx);
            Plugin.Info($"[StopHarass] Participants: {DescribeAll(participants)}");

            // Refused ask: a NO-reply scenario played inside THIS talk. The trait
            // judge already ran on BaseAnswer.Judge and either forced NO (Evil /
            // Selfish) or marked a fake YES (Schemer); this gate only reads the
            // dialogue branch that actually played.
            if (refused)
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): refused (NO scenario played).");
                return;
            }
            if (fakeYes)
            {
                Plugin.Info($"[StopHarass] No removal ({reason}): Schemer gave a fake YES.");
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
