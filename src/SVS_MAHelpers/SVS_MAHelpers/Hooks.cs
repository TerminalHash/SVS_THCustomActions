#nullable enable
using System;
using System.Collections.Generic;
using ADV;
using HarmonyLib;
using SV.Chara;
using SV.CharaSelectScene;
using SV.Talk;
using SVS_MAHelpers.Actions.Clothing;
using SVS_MAHelpers.Actions.IgnoreMe;
using SVS_MAHelpers.Actions.MoveAway;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Declarative Harmony entry point: one explicit attribute per game method,
    /// no reflection loops, and nothing patched twice.
    ///
    /// Talk start/end are patched on the <see cref="TalkTaskBase"/> virtuals
    /// only. The derived tasks call into these base methods — PCActiveTalkTask
    /// and PCPassiveTalkTask start with a direct call to TalkTaskBase.Start
    /// (verified in GameAssembly.dll), and every derived End reaches
    /// TalkTaskBase.End (the only place that removes the task from TalkManager)
    /// — so the old base+override patch set ran every talk hook twice.
    ///
    /// The character picker is read-only: <c>CharaSelect.CloseExeAsync</c> is
    /// the only seam. Its CloseParameter carries the picked actor (or null for
    /// a cancel); the plugin stores that value for the pending ask and never
    /// touches the list UI, its filters or its lifecycle.
    ///
    /// The clothing force-NO decision is taken at the answer judge
    /// (<c>SV.BaseAnswer.Judge</c>), never at a scenario var switch.
    ///
    /// Period/day transitions use one shift hook only:
    /// <c>ShiftFromSimNightToRoomMorning</c> (entering the room) ends the
    /// day-scoped takes, with the <c>saveData.Day</c> tick as a fallback. The
    /// period-scoped full strip (507) ends on the <c>SimulationManager.mode</c>
    /// label watch inside the tick, so no other shift starter is patched.
    /// </summary>
    internal static class Hooks
    {
        private static Harmony? _harmony;

        internal static void PatchAll()
        {
            if (_harmony != null) return;
            _harmony = Harmony.CreateAndPatchAll(typeof(Hooks), Plugin.GUID);
            Plugin.Info("[Hooks] Harmony patches installed.");
        }

        internal static void Unpatch()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
        }

        // ------------------------------------------------------------------ talk

        // Records where each participant is when the talk starts (MoveAway
        // destination) and runs the debug-only IgnoreMe talk-start retract.
        // Patch on the base method covers every task whose Start calls it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TalkTaskBase), nameof(TalkTaskBase.Start))]
        private static void TalkStartPostfix(TalkTaskBase __instance)
        {
            try
            {
                var participants = SnapshotParticipants(__instance, keepNulls: false);
                if (participants.Count == 0) return;
                MoveAwayService.NoteTalkStart(participants);
                IgnoreService.ClearPairOnTalkStart(participants);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Talk start failed: {ex.Message}"); }
        }

        internal sealed class TalkEndState
        {
            internal readonly int ActionNo;
            internal readonly List<AI> Charas;

            internal TalkEndState(int actionNo, List<AI> charas)
            {
                ActionNo = actionNo;
                Charas = charas;
            }
        }

        // Captures TaskActionNo + participants before End() clears them.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TalkTaskBase), nameof(TalkTaskBase.End))]
        private static void TalkEndPrefix(TalkTaskBase __instance, out TalkEndState? __state)
        {
            int actionNo = -1;
            var participants = new List<AI>();
            try
            {
                actionNo = __instance.TaskActionNo;
                participants = SnapshotParticipants(__instance, keepNulls: true);
                Plugin.Info($"[Hooks] Talk End: task={__instance.GetType().FullName} TaskActionNo={actionNo} charas={participants.Count}");
            }
            catch { }
            __state = new TalkEndState(actionNo, participants);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TalkTaskBase), nameof(TalkTaskBase.End))]
        private static void TalkEndPostfix(TalkTaskBase __instance, TalkEndState? __state)
        {
            if (__state == null || __state.Charas.Count == 0) return;
            try { TriggerService.DispatchTalkEnd(__state.ActionNo, __state.Charas, __instance); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Talk end dispatch failed: {ex.Message}"); }
        }

        // Slot order is preserved (nulls kept): slots 0-1 carry the dialogue pair.
        private static List<AI> SnapshotParticipants(TalkTaskBase task, bool keepNulls)
        {
            var list = new List<AI>();
            try
            {
                var charas = task.charas;
                if (charas == null) return list;
                foreach (var ai in charas)
                {
                    if (ai == null && !keepNulls) continue;
                    list.Add(ai!);
                }
            }
            catch { }
            return list;
        }

        // -------------------------------------------------------------- scenario

        // One watch for every custom scenario load: trigger arming, ask capture
        // and the refusal watch all share it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TextScenario), nameof(TextScenario.LoadFile),
            typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool))]
        private static void LoadFilePostfix(string bundle, string asset)
        {
            try
            {
                if (string.IsNullOrEmpty(asset)) return;

                TriggerService.OnScenarioLoad(bundle, asset);

                if (!RefusalParser.TryParseNoScenario(asset, out int number, out string kind) || number < 0) return;
                Plugin.Info($"[Refusal] Reply scenario '{asset}' ({kind}, bundle='{bundle}').");
                TriggerService.NoteRefusal(number);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Scenario load failed: {ex.Message}"); }
        }

        // ----------------------------------------------------------------- pump

        // Queued triggers and per-frame ticks run on the main thread.
        // SimulationScene.Update is the simulation frame; MapManager.Update is
        // not guaranteed to run every frame across scene states, so the pump
        // lives on the scene instead.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SV.SimulationScene), nameof(SV.SimulationScene.Update))]
        private static void SimulationUpdatePostfix() => TriggerService.Pump();

        // ------------------------------------------------------------- clothing

        // The only time-shift starter we need: entering the room is the day
        // rollover, so every clothing take ends there (506/509 day scope and
        // 507, whose period also ended). It runs before the async transition
        // starts, while the stripped AIs are still reachable; the
        // saveData.Day tick in ClothingService is the fallback for paths that
        // skip this starter. No other shift starter changes cloth state.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SV.SimulationButtonAction), nameof(SV.SimulationButtonAction.ShiftFromSimNightToRoomMorning))]
        private static void RoomTransitionPostfix()
        {
            try { ClothingService.OnDayChanged("Room transition"); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Room transition failed: {ex.Message}"); }
        }

        // --------------------------------------------------------------- picker

        // Read-only picker tap. CloseParameter.Actor is the picked actor, or
        // null when the player cancelled. Nothing below ever changes the list,
        // its regulations or its close flow; TriggerService only stores the
        // value for the single ask that is still awaiting its reply.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CharaSelect), nameof(CharaSelect.CloseExeAsync), typeof(CharaSelect.CloseParameter))]
        private static void PickerClosePrefix(CharaSelect.CloseParameter parameter)
        {
            try
            {
                var actor = parameter?.Actor;
                if (actor == null)
                {
                    Plugin.Info("[Picker] Closed WITHOUT selection (cancelled?).");
                    return;
                }
                if (TriggerService.SetPick(actor))
                {
                    string nm = "?";
                    try { nm = actor.charFile?.Parameter?.fullname ?? "?"; } catch { }
                    Plugin.Info($"[Picker] Picked: [{nm}].");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Picker] Capture failed: {ex.Message}"); }
        }

        // -------------------------------------------------------------- clothing

        // HighPoly doubles are re-dressed at load; strip them after the game's
        // own reset, before the ADV opens.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TalkTaskBase), nameof(TalkTaskBase.LoadHighPoly))]
        private static void LoadHighPolyPostfix(AI? _ai, Manager.TalkManager.TaskCharaInfo? _info)
        {
            try { ClothingService.StripDouble(_ai, _info); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] LoadHighPoly hook failed: {ex.Message}"); }
        }

        // Task redress counter (notably the post-H restore).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TalkTaskBase), nameof(TalkTaskBase.SetClothesStateAll))]
        private static void SetClothesStateAllPostfix(TalkTaskBase __instance)
        {
            try
            {
                if (__instance.charas is System.Collections.IEnumerable charas)
                    ClothingService.RestripTaskCharas(charas);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] SetClothesStateAll hook failed: {ex.Message}"); }
        }

        // Force-NO: the answer judge is the only place that decides YES/NO/難聴.
        // EachJudgment calls every answer table in turn; only the table that
        // owns this commandID returns true, so this runs at most once per
        // judgment and never for unrelated commands. 506/509 are the two
        // underwear takes that a held pair must refuse; 507 (full strip) is
        // not an underwear take and is left alone.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SV.BaseAnswer), nameof(SV.BaseAnswer.Judge))]
        private static void AnswerJudgePostfix(
            SV.YesNoJudgeManager.AnswerInfo _ansInfo,
            SV.YesNoJudgeManager.YesNoInfo _ynInfo,
            int _commandID,
            bool __result)
        {
            if (!__result) return;
            if (_commandID != ActionVars.BlackmailUnderwearActionNo
                && _commandID != ActionVars.LoveUnderwearActionNo) return;
            try { ClothingService.ForceNoIfHeld(_ansInfo, _ynInfo); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Answer judge hook failed: {ex.Message}"); }
        }

        // ------------------------------------------------------- ignore backstop

        // Both ThinkingManager interpersonal target pickers return the chosen
        // target's index, or -1 for none. A recorded NPC's PC pick is rewritten
        // to -1 ("no target") so the NPC never acquires the PC.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SV.ThinkingManager), nameof(SV.ThinkingManager.InterpersonalCommandSelectionTarget))]
        private static void PickTargetPostfix(SaveData.Actor _actor, int _commandID, ref int __result)
            => PickBackstop(_actor, _commandID, ref __result);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SV.ThinkingManager), nameof(SV.ThinkingManager.InterpersonalCommandSelectionTargetReaction))]
        private static void PickReactionPostfix(SaveData.Actor _actor, int _commandID, ref int __result)
            => PickBackstop(_actor, _commandID, ref __result);

        private static void PickBackstop(object? actorObj, object? commandObj, ref int resultIdx)
        {
            try
            {
                if (resultIdx < 0) return;
                IgnoreService.RewritePick(actorObj, commandObj, ref resultIdx);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Pick backstop failed: {ex.Message}"); }
        }

        // ------------------------------------------------------------- map links

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pathfinding.NodeLink2), nameof(Pathfinding.NodeLink2.OnEnable))]
        private static void LinkEnablePostfix(Pathfinding.NodeLink2 __instance)
        {
            try { MoveAwayService.RegisterLink(__instance); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Hooks] Link registry failed: {ex.Message}"); }
        }
    }
}
