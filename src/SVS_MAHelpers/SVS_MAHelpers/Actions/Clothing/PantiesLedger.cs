#nullable enable
using System;
using System.Collections.Generic;

namespace SVS_MAHelpers.Actions.Clothing
{
    /// <summary>
    /// Plugin-owned stolen-panties ledger. Persisted through SVS_AdditionalSaveData
    /// (its own <c>mod_&lt;save&gt;.json</c> sidecar), so the main save file is
    /// untouched; without that plugin the ledger is session-only.
    /// <para>
    /// Every held pair keeps its own take day, oldest first. This matters when
    /// the PC takes from the same NPC on a later day: the older pair no longer
    /// restricts, but the newer day must keep its hold, and returning one pair
    /// must not clear a hold that still belongs to another pair. A day of -1
    /// means the take day was unreadable; it never restricts.
    /// </para>
    /// </summary>
    public static class PantiesLedger
    {
        // AdditionalSaveData categories: days are a CSV of take days, names are
        // kept separately so day enumeration never parses display strings.
        private const string DaysCategory = "SVS_MAHelpers.Panties.Days";
        private const string NamesCategory = "SVS_MAHelpers.Panties.Names";

        private static readonly Dictionary<int, List<int>> _takeDays = new();
        private static readonly Dictionary<int, string> _names = new();
        private static volatile bool _hydrated;

        // Fired on the main-thread pump right after a successful take:
        // (npc chara index, npc display name, total held for that NPC).
        public static event Action<int, string, int>? Stolen;

        public static int GetCount(int npcIdx)
        {
            EnsureHydrated();
            lock (_takeDays)
                return _takeDays.TryGetValue(npcIdx, out var days) ? days.Count : 0;
        }

        public static int Total
        {
            get
            {
                EnsureHydrated();
                lock (_takeDays)
                {
                    int t = 0;
                    foreach (var days in _takeDays.Values) t += days.Count;
                    return t;
                }
            }
        }

        public static string GetName(int npcIdx)
        {
            EnsureHydrated();
            lock (_names)
                return _names.TryGetValue(npcIdx, out string? nm) ? nm : "";
        }

        // True when the PC holds a pair of this NPC's underwear taken TODAY.
        // A pair from an earlier day no longer restricts, but any other pair
        // keeps its own day. day < 0 = unknown: fail open, never restrict.
        public static bool HasHold(int npcIdx, int today)
        {
            if (today < 0) return false;
            EnsureHydrated();
            lock (_takeDays)
            {
                if (!_takeDays.TryGetValue(npcIdx, out var days) || days == null) return false;
                return days.Contains(today);
            }
        }

        // Returns one held pair (oldest first): removes that pair's day and
        // leaves every other pair's hold intact. False when the PC holds none
        // of that NPC's underwear, so the caller can treat the action as having
        // nothing to return.
        internal static bool ReturnOne(int npcIdx)
        {
            EnsureHydrated();
            bool left;
            lock (_takeDays)
            {
                if (!_takeDays.TryGetValue(npcIdx, out var days) || days == null || days.Count == 0)
                    return false;
                days.RemoveAt(0);
                left = days.Count > 0;
                if (!left) _takeDays.Remove(npcIdx);
            }
            Persist(npcIdx);
            return true;
        }

        internal static int Record(int npcIdx, string npcName, int day)
        {
            EnsureHydrated();
            int total;
            lock (_takeDays)
            {
                if (!_takeDays.TryGetValue(npcIdx, out var days) || days == null)
                {
                    days = new List<int>();
                    _takeDays[npcIdx] = days;
                }
                days.Add(day >= 0 ? day : -1);
                total = days.Count;
            }
            if (!string.IsNullOrEmpty(npcName))
                lock (_names) _names[npcIdx] = npcName;
            Persist(npcIdx);
            var h = Stolen;
            try { h?.Invoke(npcIdx, npcName, total); } catch { }
            return total;
        }

        // A different save object was loaded: holds/names from the previous save
        // must not force-NO or count on the new one. The sidecar of the new save
        // is read lazily on the next access.
        internal static void Reset()
        {
            lock (_takeDays) _takeDays.Clear();
            lock (_names) _names.Clear();
            _hydrated = false;
        }

        // Loads the current save's sidecar once per save generation. A missing
        // AdditionalSaveData plugin leaves the ledger memory-only.
        private static void EnsureHydrated()
        {
            if (_hydrated || !AdditionalDataStore.Available) return;

            var days = new Dictionary<int, List<int>>();
            var names = new Dictionary<int, string>();
            foreach (string key in AdditionalDataStore.GetKeys(DaysCategory))
            {
                if (!int.TryParse(key, out int idx) || idx < 0) continue;
                var parsed = ParseDays(AdditionalDataStore.GetString(DaysCategory, key));
                if (parsed.Count == 0) continue;
                days[idx] = parsed;
                string nm = AdditionalDataStore.GetString(NamesCategory, key);
                if (!string.IsNullOrEmpty(nm)) names[idx] = nm;
            }

            lock (_takeDays)
            {
                if (_hydrated) return;
                _takeDays.Clear();
                foreach (var kv in days) _takeDays[kv.Key] = kv.Value;
                _hydrated = true;
            }
            lock (_names)
            {
                _names.Clear();
                foreach (var kv in names) _names[kv.Key] = kv.Value;
            }
        }

        // Mirrors one NPC's entry into the sidecar: days CSV plus the last known
        // display name. Both keys are removed once the last pair is gone.
        private static void Persist(int npcIdx)
        {
            if (!AdditionalDataStore.Available) return;
            string key = npcIdx.ToString();

            List<int>? days;
            lock (_takeDays)
                days = _takeDays.TryGetValue(npcIdx, out var d) ? new List<int>(d) : null;
            string nm;
            lock (_names)
                nm = _names.TryGetValue(npcIdx, out var n) ? n : "";

            if (days == null || days.Count == 0)
            {
                AdditionalDataStore.RemoveValue(DaysCategory, key);
                AdditionalDataStore.RemoveValue(NamesCategory, key);
                return;
            }
            AdditionalDataStore.TrySetString(DaysCategory, key, string.Join(",", days));
            if (string.IsNullOrEmpty(nm)) AdditionalDataStore.RemoveValue(NamesCategory, key);
            else AdditionalDataStore.TrySetString(NamesCategory, key, nm);
        }

        private static List<int> ParseDays(string csv)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (string part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int day)) list.Add(day);
            return list;
        }
    }
}
