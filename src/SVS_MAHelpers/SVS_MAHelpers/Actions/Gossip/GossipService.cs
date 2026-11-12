#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.Gossip
{
    /// <summary>
    /// "I had fun with…" (action 508): after the ask talk ends, the asked NPC
    /// remembers the gossip exactly like the vanilla
    /// <c>DidYouHaveSexWithThatGuy</c> order (id 56) would record it — one
    /// <c>LogInfo{0x33}</c> (<c>LOG_DID_YOU_HAVE_H</c>, 51) with
    /// <c>charas=[active, passive, that]</c> (-1 slots skipped) and
    /// <c>valA=judgeAnswer</c>, added to BOTH memories (the PC's and the asked
    /// NPC's). Later vanilla gossip flows read this log back to spread the
    /// rumor; no rumor types are written directly.
    /// </summary>
    internal static class GossipService
    {
        // Vanilla log type written by DidYouHaveSexWithThatGuy (id 56).
        private const int DidYouHaveHLogType = 0x33; // LOG_DID_YOU_HAVE_H = 51

        internal static void Schedule(List<AI>? participants, string reason, AskState ask)
        {
            try
            {
                List<AI>? copy = participants != null ? new List<AI>(participants) : null;
                // Capture this action's own outcome before queuing: TriggerService
                // settles the ask as soon as the handler returns.
                bool refused = ask.TakeRefused();
                SaveData.Actor? picked = ask.Pick;
                Plugin.Info($"[Gossip] Trigger queued (reason={reason}, participants={(copy == null ? "unknown" : copy.Count.ToString())}, refused={refused}, pick={(picked != null)})");
                TriggerService.RunOnMainThread(() =>
                {
                    try { DoApply(copy, reason, refused, picked); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Gossip] DoApply failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Gossip] Schedule failed: {ex.Message}"); }
        }

        private static void DoApply(List<AI>? participants, string reason, bool refused, SaveData.Actor? picked)
        {
            if (!TriggerService.TryGetPairTargets(participants, out var pc, out var npcTargets) || pc == null || npcTargets.Count == 0)
            {
                // Not our dialogue: suppression flags stay armed for the real talk.
                Plugin.Info($"[Gossip] No gossip ({reason}): {DescribeAll(participants)}.");
                return;
            }
            var asker = npcTargets[0];
            int pcIdx = IdxOf(pc);
            int askerIdx = IdxOf(asker);
            if (pcIdx < 0 || askerIdx < 0)
            {
                Plugin.Info($"[Gossip] No gossip ({reason}): bad pair pc={pcIdx} asker={askerIdx}.");
                return;
            }
            Plugin.Info($"[Gossip] Participants: {DescribeAll(participants)}");

            // NO (1) / 難聴 (2): a NO-reply scenario played in this talk.
            if (refused)
            {
                Plugin.Info($"[Gossip] Suppressed ({reason}): NO/難聴 answer in action={ActionVars.GossipActionNo}.");
                return;
            }

            // The third character is exactly the captured picker value. A
            // cancelled pick must not fall back to a stale vanilla index.
            if (picked == null)
            {
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): no third character was picked.");
                return;
            }
            AI? that = ActorToAI(picked);
            int thatIdx = IdxOf(that);
            if (that == null || thatIdx < 0)
            {
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): picked third character is not live.");
                return;
            }
            if (thatIdx == pcIdx || thatIdx == askerIdx)
            {
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): bad third idx={thatIdx}.");
                return;
            }
            if (IsPc(that))
            {
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): the third character cannot be the PC.");
                return;
            }

            // Roles come from the pair, not raw list order: the PC is the
            // active side (the one who had fun) and the asked NPC is passive.
            // Its judgeAnswer is the vanilla valA for this lane; an unreadable
            // value must not become a success-shaped 0 log.
            int judge;
            try { judge = asker.charaData.charasGameParam.judgeAnswer; }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): judgeAnswer unreadable ({ex.GetType().Name}).");
                return;
            }
            if (judge < 0 || judge > 2)
            {
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): judgeAnswer out of range ({judge}).");
                return;
            }

            int written = WriteDidYouHaveH(pc, asker, pcIdx, askerIdx, thatIdx, judge);
            if (written > 0)
                Plugin.Info($"[Gossip] [{NameOf(asker)}] heard that [{NameOf(pc)}] had fun with [{NameOf(that)}] (log=0x33 to {written}/2 memories, {reason}).");
            else
                Plugin.Log.LogWarning($"[Gossip] No gossip ({reason}): memory write failed.");
        }

        // One CreateLog-equivalent entry (vanilla 67_0 shape: -1 slots skipped),
        // added to BOTH memories like vanilla AddBoth does.
        private static int WriteDidYouHaveH(AI pc, AI asker, int activeIdx, int passiveIdx, int thatIdx, int judgeAnswer)
        {
            SaveData.LogInfo log;
            try
            {
                log = new SaveData.LogInfo { type = DidYouHaveHLogType, valA = judgeAnswer };
                if (activeIdx >= 0) log.charas.Add(activeIdx);
                if (passiveIdx >= 0) log.charas.Add(passiveIdx);
                if (thatIdx >= 0) log.charas.Add(thatIdx);
            }
            catch (Exception ex)
            {
                Plugin.Info($"[Gossip] Log build failed: {ex.Message}");
                return 0;
            }
            int n = 0;
            try
            {
                var mem = pc.charaData?.charasGameParam?.memory;
                if (mem != null) { mem.AddLog(log); n++; }
            }
            catch (Exception ex) { Plugin.Info($"[Gossip] PC memory write failed: {ex.Message}"); }
            try
            {
                var mem = asker.charaData?.charasGameParam?.memory;
                if (mem != null) { mem.AddLog(log); n++; }
            }
            catch (Exception ex) { Plugin.Info($"[Gossip] Asker memory write failed: {ex.Message}"); }
            return n;
        }
    }
}
