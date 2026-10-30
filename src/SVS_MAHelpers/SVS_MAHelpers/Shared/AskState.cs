#nullable enable
using System;
using System.Collections.Generic;
using SV.Chara;

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
        private HashSet<int>? _charas;
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

        /// <summary>Starts a fresh ask; returns the snapshot size (-1 unknown).</summary>
        internal int Begin()
        {
            lock (_gate)
            {
                _generation++;
                _askUtc = DateTime.UtcNow;
                _fired = false;
                _refused = false;
                _pick = null;
                _pickSeenUtc = DateTime.MinValue;
                try { _charas = CharaUtil.SnapshotActiveTalkCharas(); }
                catch { _charas = null; }
                return _charas?.Count ?? -1;
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

        /// <summary>True when the ending talk was part of this action's ask.</summary>
        internal bool MatchesTalk(object? task)
        {
            lock (_gate)
            {
                if (_charas == null) return true;
                if (_charas.Count == 0) return false;
                foreach (int idx in TriggerService.EndingTalkCharas(task))
                    if (_charas.Contains(idx)) return true;
                return false;
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
                _charas = null;
                _refused = false;
                _fired = false;
                _pick = null;
                _pickSeenUtc = DateTime.MinValue;
            }
        }
    }
}
