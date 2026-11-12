#nullable enable
using System;

namespace SVS_MAHelpers
{
    /// <summary>
    /// One custom action's ask state. Every action registration owns its own
    /// instance, so refusal/pick data can never be read by another action.
    /// Lifetime: Begin on the action's a_NNN LoadFile, consumed by that action's
    /// trigger, settled by TriggerService after the handler returns.
    /// </summary>
    internal sealed class AskState
    {
        private readonly object _gate = new();
        private DateTime _askUtc = DateTime.MinValue;
        private CharaUtil.TalkIdentity? _identity;
        private int _pcIdx = -1;
        private int _npcIdx = -1;
        private bool _refused;
        private bool _fired;
        private SaveData.Actor? _pick;
        private DateTime _pickSeenUtc = DateTime.MinValue;
        private int _generation;

        internal bool IsActive
        {
            get { lock (_gate) return _askUtc != DateTime.MinValue; }
        }

        /// <summary>
        /// True only before this ask's trigger has fired. Reply scenarios loaded
        /// after Talk End belong to the next dialogue, not this ask.
        /// </summary>
        internal bool IsAwaitingReply
        {
            get { lock (_gate) return _askUtc != DateTime.MinValue && !_fired; }
        }

        /// <summary>Starts a fresh ask bound to the task owning this action; returns its task count (-1 unknown).</summary>
        internal int Begin(int actionNo, int npcIdx)
        {
            lock (_gate)
            {
                _generation++;
                _askUtc = DateTime.UtcNow;
                _fired = false;
                _refused = false;
                _pick = null;
                _pickSeenUtc = DateTime.MinValue;
                _pcIdx = -1;
                _npcIdx = npcIdx;
                try { _identity = CharaUtil.SnapshotActiveTalkTasks(actionNo, npcIdx); }
                catch { _identity = null; }
                if (_identity != null)
                {
                    foreach (var t in _identity.Tasks)
                    {
                        if (CharaUtil.TryGetTaskPcNpc(t, out int pc, out int npc))
                        {
                            _pcIdx = pc;
                            _npcIdx = npc;
                            break;
                        }
                    }
                }
                // No readable task pair: still remember the PC, so the
                // scenario's NPC can match the owning talk at End.
                if (_pcIdx < 0) _pcIdx = CharaUtil.PcIndex();
                return _identity?.Tasks.Count ?? -1;
            }
        }

        /// <summary>One-shot trigger gate; the ask itself is settled after the handler.</summary>
        internal bool TryFire(bool requireAsk)
        {
            lock (_gate)
            {
                if (requireAsk && _askUtc == DateTime.MinValue) return false;
                if (_fired) return false;
                _fired = true;
                return true;
            }
        }

        /// <summary>
        /// Takes an immutable handler view of the fired ask. A later Begin for
        /// the same action gets a new generation, so it cannot mutate or be
        /// consumed by the queued handler.
        /// </summary>
        internal AskState Capture(out int generation)
        {
            lock (_gate)
            {
                generation = _generation;
                var view = new AskState
                {
                    _refused = _refused,
                    _pick = _pick,
                };
                return view;
            }
        }

        /// <summary>
        /// True only when the ending talk is the task that owned this ask.
        /// Reference identity is preferred; an exact ordered pair is the
        /// fallback for a re-created task. Unknown identity fails closed.
        /// </summary>
        internal bool MatchesTalk(object? task)
        {
            lock (_gate)
            {
                if (_identity == null) return false;
                var tt = task as SV.Talk.TalkTaskBase;
                if (tt != null)
                {
                    foreach (var t in _identity.Tasks)
                        if (ReferenceEquals(t, tt)) return true;
                }
                var pair = CharaUtil.OrderedPair(tt);
                if (pair.HasValue)
                {
                    foreach (var p in _identity.Pairs)
                        if (p.A == pair.Value.A && p.B == pair.Value.B) return true;
                }
                // Il2CppInterop can hand out a different wrapper for the same
                // native task, and after-tasks re-create the task. The action's
                // identified PC+NPC pair is the last, order-independent match.
                if (_pcIdx >= 0 && _npcIdx >= 0 && CharaUtil.TaskHasPair(tt, _pcIdx, _npcIdx))
                    return true;
                return false;
            }
        }

        /// <summary>
        /// Drops this ask without firing: used when a different save object is
        /// loaded, or when the owning talk can no longer be identified.
        /// </summary>
        internal void Abort()
        {
            lock (_gate)
            {
                _generation++;
                _askUtc = DateTime.MinValue;
                _identity = null;
                _pcIdx = -1;
                _npcIdx = -1;
                _refused = false;
                _fired = false;
                _pick = null;
                _pickSeenUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Drops this ask when a different talk starts while it is awaiting its
        /// reply. The owning talk calls Start before its action scenario loads,
        /// so the owner is never seen here; a later talk carrying either the PC
        /// or this ask's NPC means the dialogue was taken over (the specified
        /// hijack: another NPC interrupts and plays another scenario) and the
        /// pending trigger must not carry into that new dialogue.
        /// </summary>
        internal bool AbortIfTalkTookOver(object? task)
        {
            lock (_gate)
            {
                if (_askUtc == DateTime.MinValue || _fired) return false;
                var tt = task as SV.Talk.TalkTaskBase;
                if (tt == null) return false;
                if (_identity != null)
                {
                    foreach (var t in _identity.Tasks)
                        if (ReferenceEquals(t, tt)) return false;
                }
                if (!CharaUtil.TaskHasCharaInPair(tt, _pcIdx)
                    && !CharaUtil.TaskHasCharaInPair(tt, _npcIdx))
                    return false;
                _generation++;
                _askUtc = DateTime.MinValue;
                _identity = null;
                _pcIdx = -1;
                _npcIdx = -1;
                _refused = false;
                _fired = false;
                _pick = null;
                _pickSeenUtc = DateTime.MinValue;
                return true;
            }
        }

        internal void NoteRefusal()
        {
            lock (_gate)
            {
                if (_askUtc != DateTime.MinValue) _refused = true;
            }
        }

        internal bool TakeRefused()
        {
            lock (_gate)
            {
                bool refused = _refused;
                _refused = false;
                return refused;
            }
        }

        /// <summary>
        /// Stores a picker close value while this ask is still waiting for its
        /// reply. The picker is read-only: there is no arm/disarm handshake,
        /// only the close value. A cancel (null) leaves the pick empty.
        /// </summary>
        internal bool SetPick(SaveData.Actor? actor)
        {
            lock (_gate)
            {
                if (_askUtc == DateTime.MinValue || _fired) return false;
                if (actor == null) return false;
                if (ReferenceEquals(_pick, actor) && (DateTime.UtcNow - _pickSeenUtc) < TimeSpan.FromSeconds(3))
                    return false;
                _pick = actor;
                _pickSeenUtc = DateTime.UtcNow;
                return true;
            }
        }

        internal SaveData.Actor? Pick
        {
            get { lock (_gate) return _pick; }
        }

        internal void Settle(int generation)
        {
            lock (_gate)
            {
                // A fresh ask for the same action has already begun; do not
                // let the previous handler clear it.
                if (generation != _generation) return;
                _askUtc = DateTime.MinValue;
                _identity = null;
                _pcIdx = -1;
                _npcIdx = -1;
                _refused = false;
                _fired = false;
                _pick = null;
                _pickSeenUtc = DateTime.MinValue;
            }
        }
    }
}
