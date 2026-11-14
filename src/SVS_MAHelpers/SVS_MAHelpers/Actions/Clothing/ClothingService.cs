#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;
using SVS_ADVScenarioSwapper;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.Clothing
{
    /// <summary>
    /// Clothing actions: 506/509 strip bra+shorts until the day ends, 507
    /// strips everything until the period ends, 510 returns a held pair (without
    /// one the answer judge is forced to 難聴). Day rollover comes from the room
    /// shift hook with the <c>saveData.Day</c> tick as fallback; the 507 period
    /// comes from the <c>SimulationManager.Mode</c> watch in
    /// <see cref="EnforceActive"/>. Strips touch both live cloth and the
    /// persistent file bytes so later HighPoly copies match.
    /// </summary>
    internal static class ClothingService
    {
        // What was taken: underwear only (bra + shorts) or everything.
        // Full outranks Underwear: a later full strip upgrades the entry, a
        // later underwear take never downgrades a full strip.
        internal enum Mode { Underwear, Full }

        private sealed class ClothEntry
        {
            public int NpcIdx = -1;
            public Mode Mode;
            public string Reason = "";
            // Scope ended: restore requested, but the NPC was not reachable.
            // The entry is kept (and retried) until live+file state are dressed.
            public bool PendingRestore;
        }

        private static readonly Dictionary<int, ClothEntry> _entries = new();

        // Last seen saveData.Day, for the day-scope rollover in the tick.
        // -1 = unknown yet (saveData unreadable, e.g. mid-transition).
        private static int _lastDay = -1;

        // Last seen SimulationManager period label, for the period scope of
        // full strips. Empty = not seen yet.
        private static string _lastPeriodLabel = "";

        // Underwear kinds = bra + shorts (ChaFileDefine.ClothesKind VERIFIED
        // members top/bot/bra/shorts/gloves/panst/socks/shoes; states
        // Clothing/HalfUndress/Naked VERIFIED via assembly reflection).
        private static readonly ChaFileDefine.ClothesKind[] UnderwearKinds =
        {
            ChaFileDefine.ClothesKind.bra,
            ChaFileDefine.ClothesKind.shorts,
        };

        private static readonly ChaFileDefine.ClothesKind[] AllKinds =
            (ChaFileDefine.ClothesKind[])Enum.GetValues(typeof(ChaFileDefine.ClothesKind));

        private static ChaFileDefine.ClothesKind[] RelevantKinds(Mode mode) =>
            mode == Mode.Full ? AllKinds : UnderwearKinds;

        internal static void ScheduleUnderwear(List<AI>? participants, string reason, int actionNo, AskState ask)
        {
            Schedule(participants, reason, actionNo, Mode.Underwear, ask);
        }

        internal static void ScheduleNaked(List<AI>? participants, string reason, int actionNo, AskState ask)
        {
            Schedule(participants, reason, actionNo, Mode.Full, ask);
        }

        internal static void ScheduleReturn(List<AI>? participants, string reason, AskState ask)
        {
            try
            {
                List<AI>? copy = participants != null ? new List<AI>(participants) : null;
                // Capture before queuing: TriggerService settles this action's
                // ask as soon as the handler returns.
                bool refused = ask.TakeRefused();
                Plugin.Info($"[Clothing] Return queued (reason={reason}, participants={(copy == null ? "unknown" : copy.Count.ToString())}, refused={refused})");
                TriggerService.RunOnMainThread(() =>
                {
                    try { DoReturn(copy, reason, refused); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] DoReturn failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] ScheduleReturn failed: {ex.Message}"); }
        }

        private static void Schedule(List<AI>? participants, string reason, int actionNo, Mode mode, AskState ask)
        {
            try
            {
                List<AI>? copy = participants != null ? new List<AI>(participants) : null;
                // Capture before queuing: TriggerService settles this action's
                // ask as soon as the handler returns.
                bool refused = ask.TakeRefused();
                Plugin.Info($"[Clothing] Trigger queued (mode={mode}, action={actionNo}, reason={reason}, participants={(copy == null ? "unknown" : copy.Count.ToString())}, refused={refused})");
                TriggerService.RunOnMainThread(() =>
                {
                    try { DoApply(copy, reason, actionNo, mode, refused); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] DoApply failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Schedule failed: {ex.Message}"); }
        }

        // Period rollover (506/507/509 doc scope): a 507 full strip ends here.
        // Underwear takes (506/509) persist to the end of the day, so only Full
        // entries are marked for restore.
        private static void OnPeriodChanged(string what)
        {
            try
            {
                List<ClothEntry> due;
                lock (_entries)
                {
                    due = new List<ClothEntry>();
                    foreach (var e in _entries.Values)
                    {
                        if (e.Mode != Mode.Full || e.PendingRestore) continue;
                        e.PendingRestore = true;
                        due.Add(e);
                    }
                }
                if (due.Count == 0)
                {
                    Plugin.Info($"[Clothing] Period '{what}': no full strips to clear (underwear takes persist to end of day).");
                    return;
                }
                Plugin.Info($"[Clothing] Period '{what}': restoring {due.Count} full strip(s); underwear takes persist to end of day.");
                foreach (var e in due)
                    TryRestore(e);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] OnPeriodChanged failed: {ex.Message}"); }
        }

        // Day rollover: every take ends here (both modes). Called by the single
        // room-transition hook and by the tick as soon as saveData.Day changes.
        internal static void OnDayChanged(string what)
        {
            try
            {
                List<ClothEntry> due;
                lock (_entries)
                {
                    if (_entries.Count == 0) return;
                    due = new List<ClothEntry>(_entries.Values);
                    foreach (var e in due) e.PendingRestore = true;
                }
                Plugin.Info($"[Clothing] {what}: restoring {due.Count} entr(y/ies) — NPC(s) dress again.");
                foreach (var e in due)
                    TryRestore(e);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] OnDayChanged failed: {ex.Message}"); }
        }

        // Tracks saveData.Day and ends the day scope on change. Runs before a
        // new take is accepted, so a save loaded on a later day cannot carry an
        // old strip into the new day.
        private static void SyncDay(int day)
        {
            if (day < 0) return;
            bool rolled = false;
            lock (_entries)
            {
                if (_lastDay < 0) { _lastDay = day; return; }
                if (day == _lastDay) return;
                _lastDay = day;
                rolled = true;
            }
            if (rolled) OnDayChanged($"Day rolled to {day}");
        }

        // Restores one entry and removes it only after BOTH layers verify as
        // dressed. An unreachable NPC keeps the entry pending for a later tick.
        private static bool TryRestore(ClothEntry e)
        {
            AI? npc = null;
            try { npc = SV.GameChara.FindCharaAI(e.NpcIdx); } catch { }
            if (npc == null)
            {
                Plugin.Info($"[Clothing] NPC idx={e.NpcIdx} not live; restore kept pending.");
                return false;
            }
            try
            {
                RestoreDressed(npc, e.Mode);
                if (!IsDressed(npc, e.Mode))
                {
                    Plugin.Info($"[Clothing] NPC idx={e.NpcIdx} restore not verified yet; kept pending.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Clothing] Restore idx={e.NpcIdx} failed: {ex.Message}");
                return false;
            }
            lock (_entries)
            {
                if (_entries.TryGetValue(e.NpcIdx, out var cur) && ReferenceEquals(cur, e))
                    _entries.Remove(e.NpcIdx);
            }
            Plugin.Info($"[Clothing] NPC idx={e.NpcIdx} dressed again ({e.Mode}).");
            return true;
        }

        // A different save object was loaded: old entries, day and period
        // baselines belong to the previous save and must not touch the new one.
        internal static void ResetState()
        {
            lock (_entries)
            {
                _entries.Clear();
                _lastDay = -1;
                _lastPeriodLabel = "";
            }
            Plugin.Info("[Clothing] Session state reset for new save.");
        }

        // Period rollover watch, tick-driven so no extra shift hook is needed.
        // Transitional mode labels (RoomMorning_SimMorning and the like) contain
        // '_' and are not rollovers; skipping them means the stable-label change
        // ends the full strips exactly once.
        private static void WatchPeriodLabel()
        {
            try
            {
                string label = Utils.TryReadPeriodLabel();
                if (string.IsNullOrEmpty(label) || label == "unknown-period") return;
                if (label.IndexOf('_') >= 0) return; // transitional, not a period
                string prev;
                lock (_entries)
                {
                    prev = _lastPeriodLabel;
                    if (string.IsNullOrEmpty(prev)) { _lastPeriodLabel = label; return; }
                    if (string.Equals(prev, label, StringComparison.Ordinal)) return;
                    _lastPeriodLabel = label;
                }
                OnPeriodChanged(label);
            }
            catch { }
        }

        private static void DoApply(List<AI>? participants, string reason, int actionNo, Mode mode, bool refused)
        {
            if (!TriggerService.TryGetPairTargets(participants, out var pc, out var npcTargets) || pc == null)
            {
                Plugin.Info($"[Clothing] No strip ({mode}, {reason}): {DescribeAll(participants)}.");
                return;
            }

            // NO (1) / 難聴 (2): a NO-reply scenario played in this talk.
            if (refused)
            {
                Plugin.Info($"[Clothing] Suppressed ({mode}, {reason}): NO/難聴 answer in action={actionNo}.");
                return;
            }
            Plugin.Info($"[Clothing] Participants: {DescribeAll(participants)}");

            int day = Utils.TryReadDay();
            // End an old day scope before accepting this take, so _lastDay can
            // never be advanced ahead of the rollover check.
            SyncDay(day);

            // Forced NO: the PC already holds this NPC's underwear taken today —
            // repeat steal/get attempts (506/509) are refused until next day.
            // Full strips (507) are not underwear takes and pass.
            if (mode == Mode.Underwear && day >= 0)
            {
                int before = npcTargets.Count;
                npcTargets.RemoveAll(ai =>
                {
                    int idx = IdxOf(ai);
                    return idx >= 0 && PantiesLedger.HasHold(idx, day);
                });
                int blocked = before - npcTargets.Count;
                if (blocked > 0)
                    Plugin.Info($"[Clothing] Forced NO for {blocked} NPC(s): PC already holds their underwear today (action={actionNo}).");
                if (npcTargets.Count == 0) return;
            }

            int pcIdx = IdxOf(pc);
            if (pcIdx < 0) return;

            int applied = 0;
            // Only new Underwear takes on a readable day can enter the ledger,
            // and only after the strip is verified.
            List<AI>? ledgerCandidates = mode == Mode.Underwear && day >= 0 ? new List<AI>() : null;
            var work = new List<(AI ai, bool fresh)>();
            lock (_entries)
            {
                foreach (var ai in npcTargets)
                {
                    try
                    {
                        int idx = ai.charaData.charasGameParam.Index;
                        if (idx < 0 || idx == pcIdx) continue;
                        bool fresh = false;
                        if (!_entries.TryGetValue(idx, out var e))
                        {
                            e = new ClothEntry { NpcIdx = idx, Mode = mode, Reason = reason };
                            _entries[idx] = e;
                            fresh = true;
                            try { ledgerCandidates?.Add(ai); } catch { }
                        }
                        else if (e.PendingRestore)
                        {
                            // A pending Full entry already ended its period.
                            // A new underwear take must not downgrade it while
                            // the outer clothes are still stripped, or the
                            // full-strip file bytes would never be restored.
                            // Dress the full strip first; when the NPC is not
                            // reachable, keep the pending entry and defer.
                            if (e.Mode == Mode.Full && mode == Mode.Underwear)
                            {
                                try { RestoreDressed(ai, Mode.Full); } catch { }
                                if (!IsDressed(ai, Mode.Full))
                                {
                                    Plugin.Info($"[Clothing] NPC idx={idx}: pending full restore is not complete; underwear take deferred.");
                                    continue;
                                }
                            }
                            // A new take revives a finished entry with the new
                            // mode instead of waiting for another dress pass.
                            e.Mode = mode;
                            e.Reason = reason;
                            e.PendingRestore = false;
                            fresh = true;
                            try { ledgerCandidates?.Add(ai); } catch { }
                        }
                        else if (e.Mode == Mode.Full)
                        {
                            // Already fully stripped: underwear takes change nothing.
                            e.Reason = mode == Mode.Full ? reason : e.Reason;
                        }
                        else if (mode == Mode.Full)
                        {
                            e.Mode = Mode.Full;
                            e.Reason = reason;
                            fresh = true;
                        }
                        else e.Reason = reason;
                        work.Add((ai, fresh));
                    }
                    catch { }
                }
            }
            foreach (var (ai, fresh) in work)
            {
                try
                {
                    var em = EffectiveMode(ai);
                    if (em == null) continue;
                    ApplyMode(ai, em.Value);
                    // applied counts only entries whose strip is actually visible
                    // in both the live and the persistent layer.
                    if (fresh && IsStripped(ai, em.Value)) applied++;
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Apply failed: {ex.Message}"); }
            }
            string scope = mode == Mode.Full ? "rest of this period" : "rest of this day";
            Plugin.Info($"[Clothing] Done: {applied}/{npcTargets.Count} NPC(s) stripped ({mode}) for the {scope} (reason={reason}).");
            if (ledgerCandidates != null && ledgerCandidates.Count > 0)
            {
                foreach (var ai in ledgerCandidates)
                {
                    try
                    {
                        if (ai == null) continue;
                        int idx = IdxOf(ai);
                        if (idx < 0) continue;
                        // Ledger only after a verified strip: a failed visual
                        // apply must not create a force-NO hold.
                        if (!IsStripped(ai, Mode.Underwear))
                        {
                            Plugin.Info($"[Clothing] NPC idx={idx}: strip not verified; panties not recorded.");
                            continue;
                        }
                        string nm = "";
                        try { nm = ai.charaData?.charFile?.Parameter?.fullname ?? ""; } catch { }
                        int held = PantiesLedger.Record(idx, nm, day);
                        Plugin.Info($"[Clothing] Stolen panties recorded: NPC idx={idx} ({nm}), held now {held} (total {PantiesLedger.Total}).");
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Ledger record failed: {ex.Message}"); }
                }
            }
        }

        // 510 "Return the panties": the PC can only give back a pair it actually
        // holds from that NPC. Returning clears the repeat-take hold (so
        // 506/509 are allowed again) and dresses the NPC's underwear; a full
        // 507 strip keeps its period scope and is not ended early.
        private static void DoReturn(List<AI>? participants, string reason, bool refused)
        {
            if (!TriggerService.TryGetPairTargets(participants, out var pc, out var npcTargets) || pc == null)
            {
                Plugin.Info($"[Clothing] No return ({reason}): {DescribeAll(participants)}.");
                return;
            }
            if (refused)
            {
                Plugin.Info($"[Clothing] Suppressed return ({reason}): NO/難聴 answer in action={ActionVars.ReturnPantiesActionNo}.");
                return;
            }

            int returned = 0;
            foreach (var ai in npcTargets)
            {
                try
                {
                    int idx = IdxOf(ai);
                    if (idx < 0 || !PantiesLedger.ReturnOne(idx)) continue;
                    returned++;
                    int remaining = PantiesLedger.GetCount(idx);
                    // With more than one held pair the NPC must stay stripped
                    // until the last one is returned; only then is the strip
                    // entry ended (a 507 full strip still keeps its period).
                    if (remaining <= 0) RestoreReturnedUnderwear(ai, idx);
                    else Plugin.Info($"[Clothing] Return idx={idx}: {remaining} pair(s) still held; NPC stays stripped.");
                    Plugin.Info($"[Clothing] Panties returned to NPC idx={idx} ({NameOf(ai)}); held now {remaining}.");
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Return failed: {ex.Message}"); }
            }
            Plugin.Info($"[Clothing] Return done ({reason}): {returned}/{npcTargets.Count} NPC(s).");
        }

        // Ends only an underwear take. A full strip is left untouched because
        // 507 owns it until the period rollover; in that overlap the hold is
        // still cleared, but the NPC stays fully stripped.
        private static void RestoreReturnedUnderwear(AI npc, int idx)
        {
            ClothEntry? e;
            lock (_entries)
            {
                if (!_entries.TryGetValue(idx, out e) || e == null) return;
                if (e.PendingRestore) return; // already heading back to dressed
                if (e.Mode == Mode.Full)
                {
                    Plugin.Info($"[Clothing] Return idx={idx}: full 507 strip stays until the period ends.");
                    return;
                }
                e.PendingRestore = true;
            }
            TryRestore(e);
        }

        // Tracked mode for a live AI, or null when untracked (caller skips:
        // an entry that vanished mid-apply must not strip).
        private static Mode? EffectiveMode(AI ai)
        {
            try
            {
                int idx = ai.charaData.charasGameParam.Index;
                lock (_entries)
                {
                    if (_entries.TryGetValue(idx, out var e) && e != null && !e.PendingRestore) return e.Mode;
                }
            }
            catch { }
            return null;
        }

        // Per-frame enforcer: re-applies the strip while it lasts. The engine
        // re-dresses actors behind our back (coordinate or costume changes,
        // talk cleanup). Cheap: nothing to do when no entries.
        internal static void EnforceActive()
        {
            try { SyncDay(Utils.TryReadDay()); }
            catch { }

            WatchPeriodLabel();

            List<ClothEntry> snapshot;
            lock (_entries)
            {
                if (_entries.Count == 0) return;
                snapshot = new List<ClothEntry>(_entries.Values);
            }
            foreach (var e in snapshot)
            {
                try { EnforceOne(e); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Enforce idx={e.NpcIdx} failed: {ex.Message}"); }
            }
        }

        private static void EnforceOne(ClothEntry e)
        {
            AI? npc = null;
            try { npc = SV.GameChara.FindCharaAI(e.NpcIdx); } catch { }
            if (npc == null)
            {
                // Keep the entry: the AI can reappear, and dropping it now could
                // leave the persistent file bytes stripped forever.
                return;
            }
            if (e.PendingRestore)
            {
                TryRestore(e);
                return;
            }
            try
            {
                var cloth = npc.chaCtrl?.cloth;
                if (cloth == null || !HasRelevantKind(cloth, e.Mode)) return;
                if (IsStripped(npc, e.Mode)) return;
                ApplyMode(npc, e.Mode);
                Plugin.Info($"[Clothing] NPC idx={e.NpcIdx} re-stripped ({e.Mode}, {e.Reason}).");
            }
            catch { }
        }

        private static bool HasRelevantKind(Character.HumanCloth cloth, Mode mode)
        {
            foreach (var kind in RelevantKinds(mode))
            {
                try { if (cloth.IsExist(kind)) return true; } catch { }
            }
            return false;
        }

        // True only when every existing relevant kind is Naked in BOTH layers
        // (live visuals and the persistent file). Unreadable state is not a
        // verified strip.
        private static bool IsStripped(AI ai, Mode mode)
        {
            try
            {
                var cloth = ai.chaCtrl?.cloth;
                if (cloth == null) return false;
                bool any = false;
                foreach (var kind in RelevantKinds(mode))
                {
                    bool exists = false;
                    try { exists = cloth.IsExist(kind); } catch { }
                    if (!exists) continue;
                    any = true;
                    var live = ReadLiveState(cloth, kind);
                    if (live != ChaFileDefine.ClothesState.Naked) return false;
                    byte? file = ReadFileState(cloth, kind);
                    if (!file.HasValue || file.Value != (byte)ChaFileDefine.ClothesState.Naked) return false;
                }
                return any;
            }
            catch { return false; }
        }

        // True only when every existing relevant kind is dressed in BOTH
        // layers; unreadable state keeps the entry pending.
        private static bool IsDressed(AI ai, Mode mode)
        {
            try
            {
                var cloth = ai.chaCtrl?.cloth;
                if (cloth == null) return false;
                foreach (var kind in RelevantKinds(mode))
                {
                    bool exists = false;
                    try { exists = cloth.IsExist(kind); } catch { }
                    if (!exists) continue;
                    var live = ReadLiveState(cloth, kind);
                    if (!live.HasValue || live.Value == ChaFileDefine.ClothesState.Naked) return false;
                    byte? file = ReadFileState(cloth, kind);
                    if (!file.HasValue || file.Value == (byte)ChaFileDefine.ClothesState.Naked) return false;
                }
                // No relevant kind exists: nothing was stripped, so nothing is
                // left to restore.
                return true;
            }
            catch { return false; }
        }

        private static ChaFileDefine.ClothesState? ReadLiveState(Character.HumanCloth cloth, ChaFileDefine.ClothesKind kind)
        {
            try { return cloth.GetClothesStateType(kind); } catch { return null; }
        }

        // Applies the strip to the live (LowPoly) actor AND the persistent file
        // state (HumanDataStatus.clothesState), so subsequently built HighPoly
        // doubles come up stripped too (LoadHighPoly copies the file). Only the
        // index==ClothesKind mapping is inferred; every access is length-guarded.
        private static void ApplyMode(AI ai, Mode mode)
        {
            var cloth = ai.chaCtrl?.cloth;
            if (cloth == null) return;
            ApplyToCloth(cloth, mode);
        }

        // Re-strips task participants that are tracked, after any game flow
        // re-dresses live cloth via the task helper (notably the post-H restore:
        // PCHTask calls SetClothesStateAll(0, false) after every H). Synchronous,
        // so there is no dressed frame between the game's reset and ours.
        internal static void RestripTaskCharas(System.Collections.IEnumerable? charas)
        {
            try
            {
                if (charas == null) return;
                List<(AI ai, Mode mode)> hits = new();
                lock (_entries)
                {
                    if (_entries.Count == 0) return;
                    foreach (var o in charas)
                    {
                        AI? ai = o as AI;
                        if (ai == null) continue;
                        int idx = IdxOf(ai);
                        if (idx < 0) continue;
                        if (_entries.TryGetValue(idx, out var e) && e != null && !e.PendingRestore)
                            hits.Add((ai, e.Mode));
                    }
                }
                foreach (var (ai, mode) in hits)
                {
                    try
                    {
                        ApplyMode(ai, mode);
                        Plugin.Info($"[Clothing] NPC idx={IdxOf(ai)} re-stripped after task redress ({mode}).");
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Strips a HighPoly talk double (TaskCharaInfo.chaCtrl). Most talks pass
        // isResetClotheType=true, so the double is re-dressed at load no matter
        // what the file says — the strip must be re-applied after the game's
        // reset, before the ADV opens.
        internal static void StripDouble(AI? ai, Manager.TalkManager.TaskCharaInfo? info)
        {
            try
            {
                if (ai == null || info == null) return;
                int idx = IdxOf(ai);
                if (idx < 0) return;
                Mode mode;
                lock (_entries)
                {
                    if (!_entries.TryGetValue(idx, out var e) || e == null || e.PendingRestore) return;
                    mode = e.Mode;
                }
                Character.HumanCloth? cloth = null;
                try { cloth = info.chaCtrl?.cloth; } catch { return; }
                if (cloth == null) return;
                ApplyToCloth(cloth, mode);
                Plugin.Info($"[Clothing] Double stripped (NPC idx={idx}, {mode}).");
            }
            catch { }
        }

        // Live visuals + persistent file bytes for one cloth, both layers.
        private static void ApplyToCloth(Character.HumanCloth cloth, Mode mode)
        {
            if (mode == Mode.Underwear)
            {
                foreach (var kind in UnderwearKinds)
                {
                    try
                    {
                        if (!cloth.IsExist(kind)) continue;
                        cloth.SetClothesState(kind, ChaFileDefine.ClothesState.Naked, true);
                    }
                    catch { }
                }
            }
            else
            {
                try { cloth.SetClothesStateAll(ChaFileDefine.ClothesState.Naked); } catch { }
            }
            WriteFileState(cloth, RelevantKinds(mode), ChaFileDefine.ClothesState.Naked);
        }

        // Restores the dressed state in both layers when an entry ends, so
        // nothing leaks past its scope.
        private static void RestoreDressed(AI npc, Mode mode)
        {
            var cloth = npc.chaCtrl?.cloth;
            if (cloth == null) return;
            if (mode == Mode.Underwear)
            {
                foreach (var kind in UnderwearKinds)
                {
                    try
                    {
                        if (!cloth.IsExist(kind)) continue;
                        cloth.SetClothesState(kind, ChaFileDefine.ClothesState.Clothing, true);
                    }
                    catch { }
                }
            }
            else
            {
                try { cloth.SetClothesStateAll(ChaFileDefine.ClothesState.Clothing); } catch { }
            }
            WriteFileState(cloth, RelevantKinds(mode), ChaFileDefine.ClothesState.Clothing);
        }

        // Persistent per-kind state byte, or null when unreadable (caller skips:
        // never drives a fix by itself).
        private static byte? ReadFileState(Character.HumanCloth cloth, ChaFileDefine.ClothesKind kind)
        {
            try
            {
                var arr = cloth.fileStatus?.clothesState;
                if (arr == null) return null;
                int i = (int)kind;
                if (i < 0 || i >= arr.Length) return null;
                return arr[i];
            }
            catch { return null; }
        }

        private static void WriteFileState(Character.HumanCloth cloth, ChaFileDefine.ClothesKind[] kinds, ChaFileDefine.ClothesState state)
        {
            try
            {
                var arr = cloth.fileStatus?.clothesState;
                if (arr == null) return;
                byte v = (byte)state;
                foreach (var kind in kinds)
                {
                    try
                    {
                        int i = (int)kind;
                        if (i < 0 || i >= arr.Length) continue;
                        arr[i] = v;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Single entry from the BaseAnswer.Judge postfix: 506/509 force NO while
        // the pair is held today; 510 forces 難聴 when no pair is held at all.
        // Only the table owning the commandID returns true, so this runs once
        // per answer and never for unrelated commands.
        internal static void AdjustJudge(int commandID, SV.YesNoJudgeManager.AnswerInfo? ansInfo, SV.YesNoJudgeManager.YesNoInfo? ynInfo)
        {
            try
            {
                if (!TryGetJudgePair(ynInfo, out int npcIdx)) return;
                if (ansInfo == null) return;

                if (commandID == ActionVars.BlackmailUnderwearActionNo
                    || commandID == ActionVars.LoveUnderwearActionNo)
                {
                    int day = Utils.TryReadDay();
                    if (day < 0 || !PantiesLedger.HasHold(npcIdx, day)) return;
                    ForceAnswer(ansInfo, 1, $"[Clothing] Forced NO answer for NPC idx={npcIdx} (PC holds their underwear, day {day}).");
                    FakeAnswer.Set(npcIdx, false, true);
                }
                else if (commandID == ActionVars.ReturnPantiesActionNo
                    && PantiesLedger.GetCount(npcIdx) <= 0)
                {
                    ForceAnswer(ansInfo, 2, $"[Clothing] Forced 難聴 answer for NPC idx={npcIdx} (no panties held).");
                }
            }
            catch { }
        }

        // Judge pair is the active PC + passive asked NPC; any other shape is
        // not ours and yields -1.
        private static bool TryGetJudgePair(SV.YesNoJudgeManager.YesNoInfo? ynInfo, out int npcIdx)
        {
            npcIdx = -1;
            try
            {
                var active = ynInfo?.active;
                var passive = ynInfo?.passive;
                if (active == null || passive == null) return false;
                var activeParam = active.charasGameParam;
                var passiveParam = passive.charasGameParam;
                if (activeParam == null || passiveParam == null || !activeParam.isPC) return false;
                npcIdx = passiveParam.Index;
                return npcIdx >= 0;
            }
            catch { return false; }
        }

        // YesNoJudgeManager.Judge can turn ans back into YES for a PC with
        // additional function 0x16 when rate > 0 (verified), so zero the rate.
        private static void ForceAnswer(SV.YesNoJudgeManager.AnswerInfo ansInfo, int answer, string log)
        {
            ansInfo.ans = answer;
            if (ansInfo.rate > 0.0f) ansInfo.rate = 0.0f;
            Plugin.Info(log);
        }
    }
}
