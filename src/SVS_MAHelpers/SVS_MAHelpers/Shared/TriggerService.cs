#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SV.Chara;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Shared action/ADV trigger detection. A trigger is an (actionNo, advName)
    /// pair: it fires when the custom ADV scenario file loads and its talk ends,
    /// or when a talk carries the action id. The ask binds to the loading talk's
    /// task on the a_NNN scenario load. Callbacks run on the main thread on the
    /// first frame after Talk End, via SimulationScene.Update.
    /// </summary>
    internal static class TriggerService
    {
        private sealed class Registration
        {
            public int ActionNo;
            public string AdvName = "";
            public Action<List<AI>, string, AskState> Handler = (_, _, _) => { };
            public readonly AskState Ask = new();
        }

        private static readonly List<Registration> _regs = new();
        private static readonly List<Action> _mainThread = new();
        private static readonly List<Action> _ticks = new();

        /// <summary>
        /// Registers a trigger. Handler receives the ending talk's participant
        /// snapshot, a reason string ("action:NNN" or "adv:NAME") and this
        /// action's own ask state.
        /// </summary>
        internal static void RegisterTrigger(int actionNo, string advName, Action<List<AI>, string, AskState> onTrigger)
        {
            if (onTrigger == null) throw new ArgumentNullException(nameof(onTrigger));
            if (string.IsNullOrWhiteSpace(advName)) throw new ArgumentException("advName required", nameof(advName));
            lock (_regs) _regs.Add(new Registration { ActionNo = actionNo, AdvName = advName.Trim(), Handler = onTrigger });
            Plugin.Info($"[Trigger] Registered action={actionNo} adv='{advName}'.");
        }

        /// <summary>Queues work for the next SimulationScene.Update (main thread).</summary>
        internal static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            lock (_mainThread) _mainThread.Add(action);
        }

        /// <summary>Registers a per-frame callback, run by the shared Update pump.</summary>
        internal static void RegisterTick(Action tick)
        {
            if (tick == null) return;
            lock (_ticks) _ticks.Add(tick);
        }

        /// <summary>
        /// Pair targets from a talk's chara slots: the PC must be one of slots
        /// 0-1; every other non-PC slot-0..1 AI is an NPC target. Slots beyond
        /// index 1 (third wheels, interveners) and talks without the PC yield
        /// nothing.
        /// </summary>
        internal static bool TryGetPairTargets(List<AI>? participants, out AI? pc, out List<AI> npcs)
        {
            npcs = new List<AI>();
            pc = null;
            try
            {
                if (participants == null || participants.Count == 0)
                    return false;

                var pair = participants.Take(2).ToList();
                foreach (var ai in pair)
                {
                    if (ai == null) continue;
                    try
                    {
                        var bc = ai.BehaviourCtrl;
                        if (bc != null && bc.IsPC) { pc = ai; break; }
                    }
                    catch { }
                }
                if (pc == null) return false; // no PC in the pair: not our dialogue

                foreach (var ai in pair)
                {
                    if (ai == null) continue;
                    if (ai == pc) continue;
                    var bc = ai.BehaviourCtrl;
                    if (bc == null) continue;
                    if (bc.IsPC) continue;
                    npcs.Add(ai);
                }
                return npcs.Count > 0;
            }
            catch { return false; }
        }

        internal static bool IsTargetAdv(string advName, string? asset)
        {
            if (string.IsNullOrEmpty(asset)) return false;
            string got = asset!.Trim();
            if (string.Equals(got, advName, StringComparison.OrdinalIgnoreCase)) return true;
            return got.EndsWith("/" + advName, StringComparison.OrdinalIgnoreCase)
                || got.EndsWith("\\" + advName, StringComparison.OrdinalIgnoreCase);
        }

        // Called by the scenario-load hook: starts the matching action's ask.
        internal static void OnScenarioLoad(string? bundle, string? asset, SaveData.Actor? heroine)
        {
            SaveGeneration.Poll();
            List<Registration> regs;
            lock (_regs) regs = new List<Registration>(_regs);
            int npcIdx = -1;
            if (heroine != null)
            {
                var ai = CharaUtil.ActorToAI(heroine);
                npcIdx = CharaUtil.IdxOf(ai);
                if (npcIdx < 0)
                {
                    try { npcIdx = heroine.charasGameParam.Index; } catch { }
                }
            }
            foreach (var r in regs)
            {
                if (!IsTargetAdv(r.AdvName, asset)) continue;
                int count = r.Ask.Begin(r.ActionNo, npcIdx);
                Plugin.Info($"[Trigger] Custom scenario '{asset}' started for action={r.ActionNo} with {(count < 0 ? "unknown" : count.ToString())} live talk task(s) (npc={npcIdx}).");
            }
        }

        // Called by the talk-start hook: a new talk that carries the PC or the
        // ask's NPC means the pending dialogue was interrupted by another NPC
        // (the only hijack mechanic), so its pending trigger flags are dropped
        // right here. Background chatter between unrelated NPCs cannot match.
        internal static void OnTalkStart(SV.Talk.TalkTaskBase? task)
        {
            if (task == null) return;
            List<Registration> regs;
            lock (_regs) regs = new List<Registration>(_regs);
            foreach (var r in regs)
            {
                try
                {
                    if (!r.Ask.AbortIfTalkTookOver(task)) continue;
                    Plugin.Info($"[Trigger] Pending ask for action={r.ActionNo} aborted: the dialogue was taken over by another talk.");
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Trigger] Talk-start cleanup failed: {ex.Message}"); }
            }
        }

        // Called by the scenario-load hook for n_x / no_x. The reply belongs to
        // the pending ask(s): generic 0 is only accepted when exactly one ask is
        // awaiting a reply (otherwise the attribution is ambiguous); an explicit
        // N only to the action with that id. Each AskState then only ever
        // answers for its own action; there is no cross-action fallback.
        internal static int NoteRefusal(int number)
        {
            SaveGeneration.Poll();
            List<Registration> regs;
            lock (_regs) regs = new List<Registration>(_regs);
            List<Registration> awaiting = new();
            foreach (var r in regs)
            {
                // A fired ask already has its handler queued; a reply scenario
                // loaded afterwards belongs to the next dialogue.
                if (r.Ask.IsAwaitingReply) awaiting.Add(r);
            }
            if (awaiting.Count == 0)
            {
                Plugin.Info($"[Refusal] Reply scenario #{number} ignored: no pending action ask.");
                return 0;
            }

            Registration? target = null;
            if (number == 0)
            {
                if (awaiting.Count != 1)
                {
                    Plugin.Info($"[Refusal] Generic reply scenario #0 ignored: {awaiting.Count} asks awaiting (ambiguous).");
                    return 0;
                }
                target = awaiting[0];
            }
            else
            {
                foreach (var r in awaiting)
                    if (r.ActionNo == number) { target = r; break; }
                if (target == null)
                {
                    Plugin.Info($"[Refusal] Reply scenario #{number} ignored: no matching pending action ask.");
                    return 0;
                }
            }
            target.Ask.NoteRefusal();
            Plugin.Info($"[Refusal] Reply scenario #{number} noted for action={target.ActionNo}.");
            return 1;
        }

        // The close callback carries no action id. Route it to the single ask
        // that is still awaiting its reply; ambiguity is refused rather than
        // guessed. The picker itself is only read at close.
        internal static bool SetPick(SaveData.Actor? actor)
        {
            Registration? target = null;
            int pending = 0;
            lock (_regs)
            {
                foreach (var r in _regs)
                {
                    if (!r.Ask.IsAwaitingReply) continue;
                    target = r;
                    pending++;
                }
            }
            if (pending != 1 || target == null)
            {
                Plugin.Info($"[Picker] Close ignored: {pending} ask(s) awaiting a reply.");
                return false;
            }
            bool stored = target.Ask.SetPick(actor);
            if (stored) Plugin.Info($"[Picker] Pick stored for action={target.ActionNo}.");
            return stored;
        }

        // Called by the Talk-End hook: snapshots were captured in the prefix.
        internal static void DispatchTalkEnd(int actionNo, List<AI> snapshot, object? talkTask)
        {
            SaveGeneration.Poll();
            List<Registration> regs;
            lock (_regs) regs = new List<Registration>(_regs);
            if (regs.Count == 0 || snapshot.Count == 0) return;
            foreach (var r in regs)
            {
                try
                {
                    bool askActive = r.Ask.IsActive;
                    bool ownsAsk = askActive && r.Ask.MatchesTalk(talkTask);

                    // Direct hit: a real task carrying our action id. An active
                    // ask may only be consumed by the task that owns it; a talk
                    // that merely shares the id fires without an ask.
                    if (actionNo == r.ActionNo)
                    {
                        if (askActive && !ownsAsk)
                        {
                            Plugin.Info($"[Trigger] Direct action={r.ActionNo} skipped: active ask belongs to another talk.");
                            continue;
                        }
                        if (!r.Ask.TryFire(askActive)) continue;
                        var askView = r.Ask.Capture(out int generation);
                        var copy = new List<AI>(snapshot);
                        string reason = "action:" + r.ActionNo;
                        Plugin.Info($"[Trigger] Fired (reason={reason}, participants={copy.Count}).");
                        RunOnMainThread(() => InvokeHandler(r, copy, reason, askView, generation));
                        continue;
                    }

                    // Custom-scenario hit: only the task captured at scenario
                    // load can answer. Identity is required, so an unrelated
                    // talk (even with the PC in it) can never consume the ask.
                    if (!askActive) continue;
                    if (!ownsAsk)
                    {
                        Plugin.Info($"[Trigger] Custom ask kept active (action={r.ActionNo}): this talk was not part of its scenario.");
                    }
                    else if (TryGetPairTargets(snapshot, out _, out var peeked) && peeked.Count > 0)
                    {
                        if (!r.Ask.TryFire(true)) continue;
                        var askView = r.Ask.Capture(out int generation);
                        string reason = "adv:" + r.AdvName;
                        var copy = new List<AI>(snapshot);
                        Plugin.Info($"[Trigger] Fired (reason={reason}, participants={copy.Count}).");
                        RunOnMainThread(() => InvokeHandler(r, copy, reason, askView, generation));
                    }
                    else Plugin.Info($"[Trigger] Custom ask kept active (action={r.ActionNo}): no targets in this talk.");
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Trigger] Dispatch failed: {ex.Message}"); }
            }
        }

        private static void InvokeHandler(Registration r, List<AI> copy, string reason, AskState askView, int generation)
        {
            try { r.Handler(copy, reason, askView); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[Trigger] Handler failed (action={r.ActionNo}): {ex.Message}"); }
            finally { r.Ask.Settle(generation); }
        }

        /// <summary>
        /// Aborts every live ask and drops queued work. Called when a different
        /// save object is loaded, so no old ask can consume a new talk.
        /// </summary>
        internal static void ResetAsks()
        {
            List<Registration> regs;
            lock (_regs) regs = new List<Registration>(_regs);
            foreach (var r in regs) r.Ask.Abort();
            lock (_mainThread) _mainThread.Clear();
            Plugin.Info("[Trigger] Session asks aborted for new save.");
        }

        // Shared pump (main thread): runs queued work, then per-frame ticks.
        internal static void Pump()
        {
            if (SaveGeneration.Poll())
            {
                // Reset callbacks ran; queued work belongs to the old save.
                lock (_mainThread) _mainThread.Clear();
            }

            List<Action> due = new();
            lock (_mainThread)
            {
                if (_mainThread.Count > 0)
                {
                    due.AddRange(_mainThread);
                    _mainThread.Clear();
                }
            }
            foreach (var a in due)
            {
                try { a(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Trigger] Queued work failed: {ex.Message}"); }
            }

            List<Action> ticks;
            lock (_ticks)
            {
                if (_ticks.Count == 0) return;
                ticks = new List<Action>(_ticks);
            }
            foreach (var t in ticks)
            {
                try { t(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Trigger] Tick failed: {ex.Message}"); }
            }
        }
    }
}
