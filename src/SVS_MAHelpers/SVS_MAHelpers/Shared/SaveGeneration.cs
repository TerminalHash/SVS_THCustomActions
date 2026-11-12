#nullable enable
using System;
using System.Collections.Generic;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Detects a different save object in the same process and resets the
    /// plugin's session state. Entries are keyed by chara index only, so
    /// without this reset a save load could reuse an old strip, ignore, hold,
    /// map or ask on the new save's same-index characters.
    /// <para>
    /// Detection is reference identity of <c>Manager.Game.saveData</c>; the
    /// first sighting only baselines. Loading the same object again in place
    /// cannot be observed, but a normal save load replaces the object.
    /// </para>
    /// </summary>
    internal static class SaveGeneration
    {
        private static readonly List<Action> _resets = new();
        private static object? _save;
        private static int _generation;

        internal static void Register(Action reset)
        {
            if (reset == null) return;
            lock (_resets) _resets.Add(reset);
        }

        /// <summary>
        /// Polls the current save. Returns true when a new save object was seen
        /// and every registered reset ran.
        /// </summary>
        internal static bool Poll()
        {
            object? now;
            try { now = Manager.Game.saveData; } catch { return false; }
            if (now == null) return false;

            Action[] resets;
            int gen;
            lock (_resets)
            {
                if (_save == null) { _save = now; return false; }
                if (ReferenceEquals(_save, now)) return false;
                _save = now;
                gen = ++_generation;
                resets = _resets.ToArray();
            }
            foreach (var r in resets)
            {
                try { r(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Save] Reset failed: {ex.Message}"); }
            }
            Plugin.Info($"[Save] Save object changed; session state reset (generation {gen}).");
            return true;
        }
    }
}
