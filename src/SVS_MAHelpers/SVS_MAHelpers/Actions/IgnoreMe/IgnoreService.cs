#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.IgnoreMe
{
    /// <summary>
    /// "Ignore me" (action 504): after the ask talk ends with YES, the asked
    /// NPC's agreement is written into vanilla command memory as KeepHimAway
    /// (0x4F) via <see cref="VanillaRecord"/> — the same tables the game's own
    /// SetCommandCount writes. This service only manages the record's lifetime:
    /// end of period, sex/group reset, and the debug-only talk-start reset.
    /// <para>
    /// The only NPC-behavior touch is the stateless ThinkingManager pick
    /// backstop (see <c>Hooks</c>): a recorded NPC's PC pick is rewritten to -1
    /// ("no target"). No blind flags, no talk veto.
    /// </para>
    /// </summary>
    internal static class IgnoreService
    {
        private sealed class IgnoreEntry
        {
            public int NpcIdx = -1;
            public int PcIdx = -1;
            public VanillaRecord.SideUnits NpcUnits = new();
            public VanillaRecord.SideUnits PcUnits = new();
            public string Reason = "";
            // H-episode latch: set on the first tick that observes live H flags,
            // cleared when they drop. While latched, the TalkManager scan runs
            // at most every HRescanInterval.
            public bool WasInH;
            public DateTime HLastScan;
            // Scope ended, but the retraction could not reach the memory/tables
            // yet. The entry is kept and retried; it is never dropped first.
            public bool PendingRetract;
            public DateTime RetractLastUtc = DateTime.MinValue;
        }

        private static readonly Dictionary<int, IgnoreEntry> _entries = new();

        internal static void Schedule(List<AI>? participants, string reason, AskState ask)
        {
            try
            {
                List<AI>? copy = participants != null ? new List<AI>(participants) : null;
                // Capture before queuing: TriggerService settles this action's
                // ask as soon as the handler returns.
                bool refused = ask.TakeRefused();
                Plugin.Info($"[IgnoreMe] Trigger queued (reason={reason}, participants={(copy == null ? "unknown" : copy.Count.ToString())}, refused={refused})");
                TriggerService.RunOnMainThread(() =>
                {
                    try { DoApply(copy, reason, refused); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] DoApply failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] Schedule failed: {ex.Message}"); }
        }

        private static void DoApply(List<AI>? participants, string reason, bool refused)
        {
            if (!TriggerService.TryGetPairTargets(participants, out var pc, out var npcTargets) || pc == null)
            {
                Plugin.Info($"[IgnoreMe] No ignore ({reason}): {DescribeAll(participants)}.");
                return;
            }

            // NO (answer 1) / 難聴 (answer 2): nothing is recorded.
            if (refused)
            {
                Plugin.Info($"[IgnoreMe] Suppressed ({reason}): NO/難聴 answer in action={ActionVars.IgnoreActionNo}.");
                return;
            }
            Plugin.Info($"[IgnoreMe] Participants: {DescribeAll(participants)}");

            int pcIdx = IdxOf(pc);
            if (pcIdx < 0) return;

            int applied = 0;
            foreach (var ai in npcTargets)
            {
                try
                {
                    int idx = ai.charaData.charasGameParam.Index;
                    if (idx < 0 || idx == pcIdx) continue;
                    // The asked NPC's own answer, exactly as vanilla
                    // SetCommandCount files the passive side's judgeAnswer
                    // (0 = agreed; 1 = NO; else ambiguous bucket).
                    int answer = 0;
                    try { answer = ai.charaData.charasGameParam.judgeAnswer; } catch { }
                    VanillaRecord.Record(idx, pcIdx, answer, out var npcUnits, out var pcUnits);
                    if (AddedUnits(npcUnits) + AddedUnits(pcUnits) <= 0)
                    {
                        Plugin.Log.LogWarning($"[IgnoreMe] NPC idx={idx}: record added nothing; not tracked.");
                        continue;
                    }
                    lock (_entries)
                    {
                        if (!_entries.TryGetValue(idx, out var e))
                        {
                            e = new IgnoreEntry { NpcIdx = idx, PcIdx = pcIdx };
                            _entries[idx] = e;
                        }
                        Accumulate(e.NpcUnits, npcUnits);
                        Accumulate(e.PcUnits, pcUnits);
                        // A fresh record revives the entry; any units still
                        // pending from an earlier scope remain tracked and will
                        // be retracted together at the next scope end.
                        e.PendingRetract = false;
                        e.Reason = reason;
                    }
                    applied++;
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] Record idx failed: {ex.Message}"); }
            }
            string period = Utils.TryReadPeriodLabel();
            Plugin.Info($"[IgnoreMe] Done: {applied}/{npcTargets.Count} NPC(s) recorded ignoring PC idx={pcIdx} for the rest of this period ({period}) (reason={reason}).");
        }

        private static int AddedUnits(VanillaRecord.SideUnits u)
        {
            int n = 0;
            try
            {
                foreach (var t in u.Tables)
                    n += t.Count + t.Yes + t.No + t.Ambiguous;
            }
            catch { }
            return n;
        }

        private static void Accumulate(VanillaRecord.SideUnits into, VanillaRecord.SideUnits add)
        {
            try
            {
                for (int i = 0; i < into.Tables.Length && i < add.Tables.Length; i++)
                {
                    into.Tables[i].Count += add.Tables[i].Count;
                    into.Tables[i].Yes += add.Tables[i].Yes;
                    into.Tables[i].No += add.Tables[i].No;
                    into.Tables[i].Ambiguous += add.Tables[i].Ambiguous;
                }
            }
            catch { }
        }

        private static readonly TimeSpan RetractRetry = TimeSpan.FromSeconds(5);

        // Attempts the exact retraction. The entry is removed only after the
        // memory and command tables were reachable; otherwise it stays pending
        // and a later tick retries, so a transition/unload cannot lose it.
        private static bool TryRetract(IgnoreEntry e)
        {
            e.RetractLastUtc = DateTime.UtcNow;
            bool ok = false;
            try { ok = VanillaRecord.Retract(e.NpcIdx, e.PcIdx, e.NpcUnits, e.PcUnits); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] Retract idx={e.NpcIdx} failed: {ex.Message}"); }
            if (!ok)
            {
                Plugin.Info($"[IgnoreMe] Retract idx={e.NpcIdx} deferred; record kept for retry.");
                return false;
            }
            lock (_entries)
            {
                if (_entries.TryGetValue(e.NpcIdx, out var cur) && ReferenceEquals(cur, e))
                    _entries.Remove(e.NpcIdx);
            }
            return true;
        }

        // Period shift: the ignore lasts until the end of the period.
        internal static void OnPeriodChanged(string what)
        {
            try
            {
                // Re-baseline the tick backstop FIRST — even with no entries, or
                // a later record would compare against a stale period.
                try
                {
                    string label = Utils.TryReadPeriodLabel();
                    if (!string.IsNullOrEmpty(label) && label != "unknown-period")
                        lock (_entries) { _lastPeriodLabel = label; }
                }
                catch { }
                List<IgnoreEntry> due;
                lock (_entries)
                {
                    if (_entries.Count == 0) return;
                    due = new List<IgnoreEntry>(_entries.Values);
                    foreach (var e in due) e.PendingRetract = true;
                }
                Plugin.Info($"[IgnoreMe] Period shift '{what}': retracting {due.Count} ignore record(s) — NPC(s) will notice the PC again.");
                foreach (var e in due) TryRetract(e);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] OnPeriodChanged failed: {ex.Message}"); }
        }

        // Talk-start reset (debug-only, gated by ClearOnTalkStart).
        internal static void ClearPairOnTalkStart(List<AI>? participants)
        {
            try
            {
                bool enabled = false;
                try { enabled = Plugin.ClearOnTalkStart.Value; } catch { return; }
                if (!enabled) return;
                if (!TriggerService.TryGetPairTargets(participants, out var pc, out var npcs) || pc == null)
                    return;
                int pcIdx = IdxOf(pc);
                if (pcIdx < 0) return;
                List<IgnoreEntry> cleared = new();
                lock (_entries)
                {
                    foreach (var ai in npcs)
                    {
                        int idx = IdxOf(ai);
                        if (idx < 0 || idx == pcIdx) continue;
                        if (_entries.TryGetValue(idx, out var e) && e != null && e.PcIdx == pcIdx)
                        {
                            e.PendingRetract = true;
                            cleared.Add(e);
                        }
                    }
                }
                foreach (var e in cleared)
                {
                    Plugin.Info($"[IgnoreMe] NPC idx={e.NpcIdx} record retracted at talk start with PC idx={pcIdx} ({e.Reason}).");
                    TryRetract(e);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] ClearPairOnTalkStart failed: {ex.Message}"); }
        }

        // Per-frame watch: retracts records whose NPC is in an H flow with its
        // PC. Vanilla has no sex/group reset, so the spec's reset is done here
        // by polling the exact flags the H prologue holds (isHPlay solo,
        // isEveryoneHPlay group) while they are live. Cheap: nothing to do when
        // no entries exist.
        internal static void EnforceActive()
        {
            try { WatchPeriodLabel(); } catch { }
            List<IgnoreEntry> snapshot;
            lock (_entries)
            {
                if (_entries.Count == 0) return;
                snapshot = new List<IgnoreEntry>(_entries.Values);
            }
            foreach (var e in snapshot)
            {
                try { EnforceOne(e); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[IgnoreMe] Enforce idx={e.NpcIdx} failed: {ex.Message}"); }
            }
        }

        // Rescan interval while a recorded NPC stays in an H flow without its
        // PC: the PC could still join late (group forming mid-scene).
        private static readonly TimeSpan HRescanInterval = TimeSpan.FromSeconds(2);

        private static void EnforceOne(IgnoreEntry e)
        {
            // A finished scope waits for a reachable memory/tables. Retry on a
            // low interval; the entry is only removed after a verified retract.
            if (e.PendingRetract)
            {
                if ((DateTime.UtcNow - e.RetractLastUtc) < RetractRetry) return;
                TryRetract(e);
                return;
            }
            AI? npc = null;
            try { npc = FindAI(e.NpcIdx); } catch { }
            if (npc == null)
            {
                // AI gone: mark pending and retract via the chara table when
                // memory is still reachable; otherwise keep the entry.
                e.PendingRetract = true;
                TryRetract(e);
                return;
            }
            // Cheap poll first: two bools, no scan, no log.
            bool solo = false, group = false;
            try
            {
                var p = npc.charaData.charasGameParam;
                if (p != null)
                {
                    try { solo = p.isHPlay; } catch { }
                    try { group = p.isEveryoneHPlay; } catch { }
                }
            }
            catch { return; }
            if (!solo && !group)
            {
                e.WasInH = false;
                return;
            }
            bool fresh = !e.WasInH;
            if (!fresh && (DateTime.UtcNow - e.HLastScan) < HRescanInterval) return;
            e.WasInH = true;
            e.HLastScan = DateTime.UtcNow;
            bool withPc = false;
            try { withPc = Utils.SharesActiveTalkWith(npc, e.PcIdx); } catch { }
            if (!withPc)
            {
                // Logged once per episode; silent rescans keep watching in case
                // the PC joins late.
                if (fresh) Plugin.Info($"[IgnoreMe] NPC idx={e.NpcIdx} is in H (solo={solo} group={group}) without its PC; record kept.");
                return;
            }
            e.PendingRetract = true;
            Plugin.Info($"[IgnoreMe] NPC idx={e.NpcIdx} record retracted after {(group ? "group action" : "sex")} with PC idx={e.PcIdx} ({e.Reason}).");
            TryRetract(e);
        }

        // Pick-backstop worker: when the thinker is a recorded NPC and its pick
        // is its PC, the pick is rewritten to -1 ("no target").
        internal static void RewritePick(object? actorObj, object? commandObj, ref int resultIdx)
        {
            try
            {
                if (actorObj == null || resultIdx < 0) return;
                int actorIdx = -1;
                try { actorIdx = Utils.ReadActorIndex(actorObj); } catch { return; }
                if (actorIdx < 0) return;
                IgnoreEntry? e;
                lock (_entries)
                {
                    if (!_entries.TryGetValue(actorIdx, out e)) return;
                    if (e == null || e.PendingRetract) return;
                }
                if (resultIdx != e.PcIdx) return;
                resultIdx = -1;
                int cmdId = -1;
                try { cmdId = Convert.ToInt32(commandObj); } catch { }
                Plugin.Info($"[IgnoreMe] NPC idx={actorIdx} pick rewritten: PC idx={e.PcIdx} -> none (cmd={cmdId}, {e.Reason}).");
            }
            catch { }
        }

        // Tick watch for the period scope. SimulationManager.Mode is the only
        // period signal available to the tick: a stable-label change retracts
        // every record. Transitional labels (RoomMorning_SimMorning and the
        // like) contain '_' and are skipped, so a shift does not retract twice.
        // First sighting only sets the base. (There are no shift hooks anymore.)
        private static string _lastPeriodLabel = "";

        // A different save object was loaded: old records belong to the previous
        // save's tables and must not be retracted from the new one.
        internal static void ResetState()
        {
            lock (_entries)
            {
                _entries.Clear();
                _lastPeriodLabel = "";
            }
            Plugin.Info("[IgnoreMe] Session state reset for new save.");
        }

        private static void WatchPeriodLabel()
        {
            try
            {
                string label = Utils.TryReadPeriodLabel();
                if (string.IsNullOrEmpty(label) || label == "unknown-period") return;
                if (label.IndexOf('_') >= 0) return; // transitional, not a period
                bool changed = false;
                string prev = "";
                lock (_entries)
                {
                    prev = _lastPeriodLabel;
                    if (string.IsNullOrEmpty(prev)) { _lastPeriodLabel = label; return; }
                    if (!string.Equals(prev, label, StringComparison.Ordinal)) changed = true;
                }
                if (!changed) return;
                Plugin.Info($"[IgnoreMe] Period changed '{prev}'->'{label}'; retracting.");
                OnPeriodChanged("tick:" + label);
            }
            catch { }
        }
    }
}
