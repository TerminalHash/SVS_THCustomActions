#nullable enable
using System;
using System.Collections.Generic;

namespace SVS_MAHelpers.Actions.Clothing
{
    /// <summary>
    /// Plugin-owned stolen-panties ledger. Session-only (no save persistence).
    /// A consumer (economy mod) reads counts via GetCount/Total/GetName or
    /// subscribes to Stolen.
    /// </summary>
    public static class PantiesLedger
    {
        private static readonly Dictionary<int, int> _counts = new();
        private static readonly Dictionary<int, string> _names = new();
        // Last take day (saveData.Day) per NPC. A hold only restricts repeat
        // takes while it is from TODAY: next day the same NPC can be stolen from
        // again.
        private static readonly Dictionary<int, int> _days = new();

        // Fired on the main-thread pump right after a successful take:
        // (npc chara index, npc display name, total held for that NPC).
        public static event Action<int, string, int>? Stolen;

        public static int GetCount(int npcIdx)
        {
            lock (_counts)
                return _counts.TryGetValue(npcIdx, out int n) ? n : 0;
        }

        public static int Total
        {
            get
            {
                lock (_counts)
                {
                    int t = 0;
                    foreach (int n in _counts.Values) t += n;
                    return t;
                }
            }
        }

        public static string GetName(int npcIdx)
        {
            lock (_names)
                return _names.TryGetValue(npcIdx, out string? nm) ? nm : "";
        }

        // True when the PC holds this NPC's underwear taken TODAY
        // (day < 0 = unknown: fail open, never restrict).
        public static bool HasHold(int npcIdx, int today)
        {
            if (today < 0) return false;
            lock (_days)
                return _days.TryGetValue(npcIdx, out int d) && d == today;
        }

        internal static int Record(int npcIdx, string npcName, int day)
        {
            int total;
            lock (_counts)
            {
                _counts.TryGetValue(npcIdx, out total);
                total++;
                _counts[npcIdx] = total;
            }
            if (!string.IsNullOrEmpty(npcName))
                lock (_names) _names[npcIdx] = npcName;
            if (day >= 0)
                lock (_days) _days[npcIdx] = day;
            var h = Stolen;
            try { h?.Invoke(npcIdx, npcName, total); } catch { }
            return total;
        }
    }
}
