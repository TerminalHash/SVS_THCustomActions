#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.Clothing
{
    /// <summary>
    /// Clothing actions 506/507/509.
    /// <list type="bullet">
    /// <item>506 and 509 strip bra + shorts; the take persists to the end of
    /// the day.</item>
    /// <item>507 strips everything; the strip persists to the end of the
    /// period.</item>
    /// </list>
    /// Both scopes are detected without extra Harmony hooks: the day rollover
    /// comes from the single room-transition hook (with the <c>saveData.Day</c>
    /// tick as fallback) and the period rollover from the
    /// <c>SimulationManager.mode</c> label watch in <see cref="EnforceActive"/>.
    /// The strip is applied to the live (LowPoly) cloth and to the persistent
    /// file bytes, so later HighPoly copies come up stripped too.
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
        // Underwear takes (506/509) are not underwear-kind "full" strips and
        // persist to the end of the day, so only Full entries are dropped.
        private static void OnPeriodChanged(string what)
        {
            try
            {
                List<ClothEntry> dropped;
                lock (_entries)
                {
                    dropped = new List<ClothEntry>();
                    var gone = new List<int>();
                    foreach (var kv in _entries)
                    {
                        if (kv.Value.Mode != Mode.Full) continue;
                        dropped.Add(kv.Value);
                        gone.Add(kv.Key);
                    }
                    foreach (var k in gone) _entries.Remove(k);
                }
                if (dropped.Count == 0)
                {
                    Plugin.Info($"[Clothing] Period '{what}': no full strips to clear (underwear takes persist to end of day).");
                    return;
                }
                Plugin.Info($"[Clothing] Period '{what}': clearing {dropped.Count} full strip(s); underwear takes persist to end of day.");
                foreach (var e in dropped)
                {
                    try { RestoreDressed(e.NpcIdx, Mode.Full); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Restore idx={e.NpcIdx} failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] OnPeriodChanged failed: {ex.Message}"); }
        }

        // Day rollover: every take ends here (both modes). Called by the single
        // room-transition hook and by the tick as soon as saveData.Day changes.
        internal static void OnDayChanged(string what)
        {
            try
            {
                List<ClothEntry> dropped;
                lock (_entries)
                {
                    if (_entries.Count == 0) return;
                    dropped = new List<ClothEntry>(_entries.Values);
                    _entries.Clear();
                }
                Plugin.Info($"[Clothing] {what}: clearing {dropped.Count} entries — NPC(s) dress again.");
                foreach (var e in dropped)
                {
                    try { RestoreDressed(e.NpcIdx, e.Mode); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Restore idx={e.NpcIdx} failed: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] OnDayChanged failed: {ex.Message}"); }
        }

        // Period rollover watch, tick-driven so no extra shift hook is needed.
        // A transition passes through intermediate mode labels; the first
        // change ends the full strips and the following ones only re-baseline.
        private static void WatchPeriodLabel()
        {
            try
            {
                string label = Utils.TryReadPeriodLabel();
                if (string.IsNullOrEmpty(label) || label == "unknown-period") return;
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
            if (day >= 0) _lastDay = day;

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
            // Only genuinely new takes are ledger-worthy: fresh Underwear
            // entries taken on a readable day.
            List<AI>? freshUnder = mode == Mode.Underwear && day >= 0 ? new List<AI>() : null;
            lock (_entries)
            {
                foreach (var ai in npcTargets)
                {
                    try
                    {
                        int idx = ai.charaData.charasGameParam.Index;
                        if (idx < 0 || idx == pcIdx) continue;
                        if (!_entries.TryGetValue(idx, out var e))
                        {
                            e = new ClothEntry { NpcIdx = idx, Mode = mode, Reason = reason };
                            _entries[idx] = e;
                            applied++;
                            try { freshUnder?.Add(ai); } catch { }
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
                            applied++;
                        }
                        else e.Reason = reason;
                    }
                    catch { }
                }
            }
            foreach (var ai in npcTargets)
            {
                try
                {
                    var em = EffectiveMode(ai);
                    if (em == null) continue;
                    ApplyMode(ai, em.Value);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Apply failed: {ex.Message}"); }
            }
            string scope = mode == Mode.Full ? "rest of this period" : "rest of this day";
            Plugin.Info($"[Clothing] Done: {applied}/{npcTargets.Count} NPC(s) stripped ({mode}) for the {scope} (reason={reason}).");
            if (freshUnder != null && freshUnder.Count > 0)
            {
                foreach (var ai in freshUnder)
                {
                    try
                    {
                        if (ai == null) continue;
                        int idx = IdxOf(ai);
                        if (idx < 0) continue;
                        string nm = "";
                        try { nm = ai.charaData?.charFile?.Parameter?.fullname ?? ""; } catch { }
                        int held = PantiesLedger.Record(idx, nm, day);
                        Plugin.Info($"[Clothing] Stolen panties recorded: NPC idx={idx} ({nm}), held now {held} (total {PantiesLedger.Total}).");
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Clothing] Ledger record failed: {ex.Message}"); }
                }
            }
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
                    if (_entries.TryGetValue(idx, out var e) && e != null) return e.Mode;
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
            try
            {
                int day = Utils.TryReadDay();
                if (day >= 0)
                {
                    if (_lastDay < 0) _lastDay = day;
                    else if (day != _lastDay)
                    {
                        // Day rolled over: 506/509 end here (and any full strip
                        // as well, since a day boundary is also a period end).
                        _lastDay = day;
                        OnDayChanged($"Day rolled to {day}");
                    }
                }
            }
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
                lock (_entries) _entries.Remove(e.NpcIdx);
                Plugin.Info($"[Clothing] NPC idx={e.NpcIdx} gone; entry dropped.");
                return;
            }
            try
            {
                if (!NeedsFix(npc, e.Mode)) return;
                ApplyMode(npc, e.Mode);
                Plugin.Info($"[Clothing] NPC idx={e.NpcIdx} re-stripped ({e.Mode}, {e.Reason}).");
            }
            catch { }
        }

        // True when the actor is not (or no longer) in the stripped state, in
        // EITHER layer (live visuals or the persistent file).
        private static bool NeedsFix(AI ai, Mode mode)
        {
            try
            {
                var cloth = ai.chaCtrl?.cloth;
                if (cloth == null) return false;
                foreach (var kind in RelevantKinds(mode))
                {
                    try
                    {
                        if (cloth.IsExist(kind)
                            && cloth.GetClothesStateType(kind) != ChaFileDefine.ClothesState.Naked) return true;
                    }
                    catch { }
                    try
                    {
                        byte? file = ReadFileState(cloth, kind);
                        if (file.HasValue && file.Value != (byte)ChaFileDefine.ClothesState.Naked) return true;
                    }
                    catch { }
                }
                return false;
            }
            catch { return false; }
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
                        if (_entries.TryGetValue(idx, out var e) && e != null)
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
                    if (!_entries.TryGetValue(idx, out var e) || e == null) return;
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
        private static void RestoreDressed(int npcIdx, Mode mode)
        {
            AI? npc = null;
            try { npc = SV.GameChara.FindCharaAI(npcIdx); } catch { }
            if (npc == null) return;
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

        // True force-NO at the answer judge: the NPC's own answer for the
        // 506/509 command is overwritten with NO (1) when the PC is the asker
        // and already holds that NPC's underwear taken today. Called from the
        // BaseAnswer.Judge postfix only when the judge returned true (the
        // table that owns the commandID), so this runs once per answer and
        // never for unrelated commands.
        internal static void ForceNoIfHeld(SV.YesNoJudgeManager.AnswerInfo? ansInfo, SV.YesNoJudgeManager.YesNoInfo? ynInfo)
        {
            try
            {
                if (ansInfo == null || ynInfo == null) return;
                var active = ynInfo.active;
                var passive = ynInfo.passive;
                if (active == null || passive == null) return;
                var activeParam = active.charasGameParam;
                var passiveParam = passive.charasGameParam;
                if (activeParam == null || passiveParam == null) return;
                if (!activeParam.isPC) return;
                int npcIdx = passiveParam.Index;
                if (npcIdx < 0) return;
                int day = Utils.TryReadDay();
                if (day < 0) return;
                if (!PantiesLedger.HasHold(npcIdx, day)) return;
                ansInfo.ans = 1; // NO
                // YesNoJudgeManager.Judge turns ans back into YES for a PC with
                // additional function 0x16 unlocked when rate > 0 (verified in
                // the reconstructed Judge), so zero the rate to keep the
                // forced answer.
                if (ansInfo.rate > 0.0f) ansInfo.rate = 0.0f;
                Plugin.Info($"[Clothing] Forced NO answer for NPC idx={npcIdx} (PC holds their underwear, day {day}).");
            }
            catch { }
        }
    }
}
