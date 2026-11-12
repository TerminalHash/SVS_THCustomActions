#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Manager;
using SV.Chara;
using static SVS_MAHelpers.CharaUtil;

namespace SVS_MAHelpers.Actions.MoveAway
{
    /// <summary>
    /// "Move away" (action 503): sends the asked NPC(s) out of the PC's current
    /// map. Destination priority: the map the NPC occupied before the talk, then
    /// the nearest paired door, then a named door, then game-authored adjacency,
    /// and only as a last resort a random public map.
    /// </summary>
    internal static class MoveAwayService
    {
        private static readonly Random _rng = new();

        // Link registry: every NodeLink2 records its endpoint positions on
        // creation (OnEnable hook). Plain links carry no map IDs on this build,
        // so exits are paired to maps positionally at use time.
        private sealed class LinkRec
        {
            public string Name = "?";
            public UnityEngine.Vector3 Start;
            public bool HasStart;
            public UnityEngine.Vector3 End;
            public bool HasEnd;
        }

        private static readonly Dictionary<string, LinkRec> _links = new();

        internal static void RegisterLink(Pathfinding.NodeLink2? link)
        {
            try
            {
                if (link == null) return;
                string n = "?";
                try { n = link.gameObject?.name ?? "?"; } catch { }

                var rec = new LinkRec { Name = n };
                try
                {
                    var st = link.StartTransform;
                    if (st != null) { rec.Start = st.position; rec.HasStart = true; }
                }
                catch { }
                try
                {
                    var et = link.EndTransform;
                    if (et != null) { rec.End = et.position; rec.HasEnd = true; }
                }
                catch { }
                if (!rec.HasStart || !rec.HasEnd)
                {
                    try
                    {
                        var p = link.transform.position;
                        if (!rec.HasStart) { rec.Start = p; rec.HasStart = true; }
                        if (!rec.HasEnd) { rec.End = p; rec.HasEnd = true; }
                    }
                    catch { }
                }
                if (!rec.HasStart || !rec.HasEnd) return;
                // Both endpoints must be real and distinct. A link whose
                // endpoints collapsed to one point is a positional artifact,
                // not a door, and would poison every exit-pairing heuristic.
                float dx = rec.End.x - rec.Start.x;
                float dy = rec.End.y - rec.Start.y;
                float dz = rec.End.z - rec.Start.z;
                if (dx * dx + dy * dy + dz * dz < 0.0001f) return;
                string key = $"{n}@{rec.Start.x:F0},{rec.Start.y:F0},{rec.Start.z:F0}";
                lock (_links)
                {
                    if (!_links.ContainsKey(key)) _links[key] = rec;
                }
            }
            catch { }
        }

        internal static void Schedule(List<AI>? participants, string reason, AskState ask)
        {
            try
            {
                List<AI>? copy = participants != null ? new List<AI>(participants) : null;
                // Capture before queuing: TriggerService settles this action's
                // ask as soon as the handler returns.
                bool refused = ask.TakeRefused();
                Plugin.Info($"[MoveAway] Trigger queued (reason={reason}, participants={(copy == null ? "unknown" : copy.Count.ToString())}, refused={refused})");
                TriggerService.RunOnMainThread(() =>
                {
                    try { DoMove(copy, reason, refused); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[MoveAway] DoMove failed: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[MoveAway] Schedule failed: {ex.Message}"); }
        }

        private static string DescribeParticipants(List<AI>? participants)
        {
            if (participants == null) return "unknown";
            var parts = new List<string>();
            try
            {
                foreach (var ai in participants)
                {
                    if (ai == null) { parts.Add("null"); continue; }
                    int map = -999;
                    try
                    {
                        var bc = ai.BehaviourCtrl;
                        if (bc != null) map = bc.NowMapID;
                    }
                    catch { }
                    parts.Add($"[{NameOf(ai)} idx={IdxOf(ai)} map={map} pc={IsPc(ai)}]");
                }
            }
            catch (Exception ex) { parts.Add("describe-failed:" + ex.GetType().Name); }
            return string.Join(" ", parts.ToArray());
        }

        // Previous-map tracking (key: charasGameParam.Index): where each NPC was
        // when its talk STARTED (recorded by the Talk Start hook — NPCs only walk
        // to the PC room afterwards) = the map to send them back to. Timestamped
        // and pruned so a long session cannot grow the map without bound.
        private sealed class StartMap
        {
            public int Map;
            public DateTime Utc;
        }

        private static readonly Dictionary<int, StartMap> _startMaps = new();
        private static readonly TimeSpan StartMapMaxAge = TimeSpan.FromHours(6);

        internal static void NoteTalkStart(List<AI> charas)
        {
            try
            {
                if (charas == null) return;
                foreach (var ai in charas)
                {
                    if (ai == null) continue;
                    try
                    {
                        int idx = ai.charaData.charasGameParam.Index;
                        var bc = ai.BehaviourCtrl;
                        if (bc == null) continue;
                        lock (_startMaps)
                            _startMaps[idx] = new StartMap { Map = bc.NowMapID, Utc = DateTime.UtcNow };
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void PruneStartMaps()
        {
            DateTime cutoff = DateTime.UtcNow - StartMapMaxAge;
            lock (_startMaps)
            {
                List<int> gone = new();
                foreach (var kv in _startMaps)
                    if (kv.Value == null || kv.Value.Utc < cutoff) gone.Add(kv.Key);
                foreach (var k in gone) _startMaps.Remove(k);
            }
        }

        // UroUroPointMove/SetCharaMapMove return void, so the only proof of a
        // move is the NPC's map on a later frame. A request stays pending until
        // the map actually changes or the timeout expires.
        private sealed class PendingMove
        {
            public AI? Ai;
            public int FromMap;
            public int DestMap;
            public DateTime DeadlineUtc;
        }

        private static readonly List<PendingMove> _pendingMoves = new();
        private static readonly TimeSpan MoveVerifyTimeout = TimeSpan.FromSeconds(10);

        private static void QueueMoveVerification(AI ai, int fromMap, int destMap)
        {
            lock (_pendingMoves)
            {
                _pendingMoves.RemoveAll(p => ReferenceEquals(p.Ai, ai));
                _pendingMoves.Add(new PendingMove
                {
                    Ai = ai,
                    FromMap = fromMap,
                    DestMap = destMap,
                    DeadlineUtc = DateTime.UtcNow + MoveVerifyTimeout,
                });
            }
        }

        // Per-frame verification of requested moves, plus start-map pruning.
        internal static void EnforceMoves()
        {
            List<PendingMove> due;
            lock (_pendingMoves)
                due = _pendingMoves.Count > 0 ? new List<PendingMove>(_pendingMoves) : new List<PendingMove>();
            foreach (var p in due)
            {
                bool done = false;
                try
                {
                    var ai = p.Ai;
                    if (ai == null) done = true;
                    else
                    {
                        var bc = ai.BehaviourCtrl;
                        if (bc == null) done = true;
                        else
                        {
                            int now = bc.NowMapID;
                            if (now != p.FromMap)
                            {
                                Plugin.Info($"[MoveAway] Verified NPC idx={IdxOf(ai)} left map {p.FromMap} (now {now}, wanted {p.DestMap}).");
                                done = true;
                            }
                            else if (DateTime.UtcNow >= p.DeadlineUtc)
                            {
                                Plugin.Log.LogWarning($"[MoveAway] Move not observed: NPC idx={IdxOf(ai)} still on map {p.FromMap} (wanted {p.DestMap}).");
                                done = true;
                            }
                        }
                    }
                }
                catch { done = true; }
                if (done)
                {
                    lock (_pendingMoves) _pendingMoves.Remove(p);
                }
            }
            PruneStartMaps();
        }

        // A different save object was loaded: previous maps and in-flight move
        // requests belong to the previous save's NPCs.
        internal static void ResetState()
        {
            lock (_startMaps) _startMaps.Clear();
            lock (_pendingMoves) _pendingMoves.Clear();
            Plugin.Info("[MoveAway] Session map state reset for new save.");
        }

        private static void DoMove(List<AI>? participants, string reason, bool refused)
        {
            if (!ComputeTargets(participants, out int roomMap, out var targets, out var pc))
            {
                Plugin.Info($"[MoveAway] No move ({reason}): {DescribeParticipants(participants)}.");
                return;
            }

            // NO (1) / 難聴 (2): a NO-reply scenario played inside this talk.
            if (refused)
            {
                Plugin.Info($"[MoveAway] Suppressed ({reason}): NO/難聴 answer in action={ActionVars.MoveAwayActionNo}.");
                return;
            }
            Plugin.Info($"[MoveAway] Participants in room {roomMap}: {DescribeParticipants(participants)}");

            Plugin.Info($"[MoveAway] Moving {targets.Count} NPC(s) out of map {roomMap} (reason={reason})");
            var mapMgr = MapManager.Instance;
            if (mapMgr == null)
            {
                Plugin.Log.LogWarning("[MoveAway] MapManager.Instance is null; aborting.");
                return;
            }

            int moved = 0;
            var moveSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var ai in targets)
            {
                try { if (MoveOne(mapMgr, ai, roomMap)) moved++; }
                catch (Exception ex) { Plugin.Log.LogWarning($"[MoveAway] MoveOne failed: {ex.Message}"); }
            }
            moveSw.Stop();
            Plugin.Info($"[MoveAway] Done: {moved}/{targets.Count} NPC move(s) requested out of room {roomMap} (took {moveSw.ElapsedMilliseconds}ms; verified on later ticks).");
        }

        // Room + targets: pair scoping (PC must be one of slots 0-1) comes from
        // TriggerService; the room is the PC's current map and only NPCs still on
        // that map move. Third wheels/interveners are never moved.
        internal static bool ComputeTargets(List<AI>? participants, out int roomMap, out List<AI> targets, out AI? pc)
        {
            roomMap = -1;
            targets = new List<AI>();
            pc = null;
            try
            {
                if (!TriggerService.TryGetPairTargets(participants, out var pairPc, out var npcs))
                    return false;
                if (pairPc == null) return false;
                pc = pairPc;
                try
                {
                    var bc = pc.BehaviourCtrl;
                    if (bc == null) return false;
                    roomMap = bc.NowMapID;
                }
                catch { return false; }
                if (roomMap < 0) return false;

                foreach (var ai in npcs)
                {
                    if (ai == null) continue;
                    var bc = ai.BehaviourCtrl;
                    if (bc == null) continue;
                    if (bc.NowMapID != roomMap) continue;
                    targets.Add(ai);
                }
                return targets.Count > 0;
            }
            catch { return false; }
        }

        // One-time-per-move dump of what the game thinks these map IDs are.
        private static void LogMapTable(int roomMap, int[] nearby)
        {
            try
            {
                var ids = new[] { roomMap }.Concat(nearby.Take(90)).Distinct().ToArray();
                var parts = new List<string>();
                foreach (var m in ids)
                {
                    string kind = "?", nm = "?", room = "?", dim = "?";
                    try
                    {
                        var table = MapManager.mapListTable;
                        if (table != null && table.ContainsKey(m))
                        {
                            var info = table[m];
                            if (info != null)
                            {
                                var it = info.GetType();
                                try { kind = it.GetProperty("Kind")?.GetValue(info, null)?.ToString() ?? "?"; } catch { }
                                try { nm = it.GetProperty("Name")?.GetValue(info, null)?.ToString() ?? "?"; } catch { }
                            }
                        }
                    }
                    catch { }
                    try { room = MapManager.IsRoomMap(m) ? "R" : "-"; } catch { }
                    try
                    {
                        var mm = MapManager.Instance;
                        if (mm != null) dim = mm.Is2DMap(m) ? "2D" : "3D";
                    }
                    catch { }
                    parts.Add($"{m}[kind={kind} room={room} {dim} name={nm}]");
                }
                Plugin.Info("[MoveAway] Map table: " + string.Join(" ", parts.ToArray()));
            }
            catch { }
        }

        private static int ResolveDest(AI ai, int roomMap, out string how)
        {
            int prev = -1;
            try
            {
                int idx = ai.charaData.charasGameParam.Index;
                StartMap? sm = null;
                lock (_startMaps) _startMaps.TryGetValue(idx, out sm);
                if (sm != null && (DateTime.UtcNow - sm.Utc) <= StartMapMaxAge) prev = sm.Map;
            }
            catch { }
            if (prev >= 0 && prev != roomMap)
            {
                how = $"back to previous map {prev}";
                return prev;
            }
            // No previous map: nearest EXIT door in this room, paired to the map
            // on its far side. Pair doors against the UNFILTERED adjacency list.
            var adjAll = NearbyMaps(roomMap).Where(m => m != roomMap).ToArray();
            var exit = NearestRoomExit(ai, roomMap, adjAll, out string exitInfo);
            if (exit.HasValue)
            {
                how = $"through nearest exit to map {exit.Value} ({exitInfo})";
                return exit.Value;
            }
            // No paired exit (registry empty): nearest NAMED door. Room doors
            // here are named 'A-B' for the maps they connect.
            var named = NearestNamedDoor(ai, roomMap, out string namedInfo);
            if (named.HasValue)
            {
                how = $"through nearest named door to map {named.Value} ({namedInfo})";
                return named.Value;
            }
            // Everything the game says this room connects to, plus every known
            // map (including custom maps registered by SVS_MapLoader).
            var nearby = FilterPublicMaps(NearbyMaps(roomMap).Where(m => m != roomMap).ToArray(), "adjacent");
            var cands = ActionVars.CandidateMaps.Concat(AllMapIds()).Distinct().Where(m => m != roomMap).ToArray();
            var outside = FilterPublicMaps(cands, "candidate");
            LogMapTable(roomMap, nearby.Concat(outside.Length > 0 ? outside : cands).Distinct().ToArray());
            if (nearby.Length > 0)
            {
                how = $"to adjacent map {nearby[0]} (no paired exit found)";
                return nearby[0];
            }
            var pool = nearby.Concat(outside.Length > 0 ? outside : cands).Distinct().ToArray();
            if (pool.Length == 0)
            {
                how = "nowhere (no candidates)";
                return -1;
            }
            int dest = PickNearestMap(ai, pool, out bool measured);
            if (dest < 0) dest = pool[_rng.Next(pool.Length)];
            how = prev >= 0
                ? $"to {(measured ? "nearest" : "random")} map {dest} (was already in room {roomMap} before the talk)"
                : $"to {(measured ? "nearest" : "random")} map {dest} (no previous map known)";
            return dest;
        }

        // Walk-point positions of one map (best effort, first slots only).
        private static List<UnityEngine.Vector3> SampleMapPoints(int map, int maxPoints)
        {
            var pts = new List<UnityEngine.Vector3>();
            try
            {
                var mapMgr = MapManager.Instance;
                if (mapMgr == null) return pts;
                for (int i = 0; i < maxPoints; i++)
                {
                    try
                    {
                        var t = mapMgr.GetUrouroPointByIndex(-1, i, map);
                        if (t == null) continue;
                        var pi = t.pInfo;
                        if (pi == null) continue;
                        pts.Add(pi.transform.position);
                    }
                    catch { }
                }
            }
            catch { }
            return pts;
        }

        // Nearest door by NAME: room links are named 'A-B' for the maps they
        // connect. Exact pairing, no geometry involved. Returns the far side.
        private static int? NearestNamedDoor(AI ai, int roomMap, out string doorInfo)
        {
            doorInfo = "";
            try
            {
                var bc = ai.BehaviourCtrl;
                if (bc == null) return null;
                UnityEngine.Vector3 npcPos = default;
                try { npcPos = bc.transform.position; }
                catch { return null; }
                List<KeyValuePair<string, LinkRec>> links;
                lock (_links) links = new List<KeyValuePair<string, LinkRec>>(_links);
                if (links.Count == 0) return null;
                string bestName = "";
                int bestMap = -1;
                float bestD = float.MaxValue;
                bool found = false;
                foreach (var kv in links)
                {
                    var link = kv.Value;
                    if (!link.HasStart) continue;
                    int a = -1, b = -1;
                    try
                    {
                        var parts = link.Name.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 2) continue;
                        if (!int.TryParse(parts[0].Trim(), out a)) continue;
                        if (!int.TryParse(parts[1].Trim(), out b)) continue;
                    }
                    catch { continue; }
                    int other = -1;
                    if (a == roomMap) other = b;
                    else if (b == roomMap) other = a;
                    else continue;
                    if (other == roomMap) continue;
                    if (IsPrivateMap(other))
                    {
                        Plugin.Info($"[MoveAway] Door '{link.Name}' leads to private map {other}; skipped.");
                        continue;
                    }
                    float dx = link.Start.x - npcPos.x;
                    float dy = link.Start.y - npcPos.y;
                    float dz = link.Start.z - npcPos.z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (!found || d < bestD) { bestD = d; bestMap = other; bestName = link.Name; found = true; }
                }
                if (!found) return null;
                doorInfo = $"'{bestName}' (d{(float)Math.Sqrt(bestD):F0})";
                return bestMap;
            }
            catch (Exception ex)
            {
                Plugin.Info($"[MoveAway] Named-door lookup failed: {ex.Message}");
                return null;
            }
        }

        private static int NearestMapOf(UnityEngine.Vector3 p, Dictionary<int, List<UnityEngine.Vector3>> byMap, int exclude)
        {
            int best = -1;
            float bestD = float.MaxValue;
            foreach (var kv in byMap)
            {
                if (kv.Key == exclude) continue;
                foreach (var q in kv.Value)
                {
                    float dx = q.x - p.x;
                    float dy = q.y - p.y;
                    float dz = q.z - p.z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; best = kv.Key; }
                }
            }
            return best;
        }

        // Nearest door out of the room, paired to the map on its far side:
        // a link whose one endpoint grounds in this room and whose other
        // endpoint grounds in an adjacent map. Grounding = nearest map (by its
        // live walk points) among room + adjacent maps.
        private static int? NearestRoomExit(AI ai, int roomMap, int[] adjacent, out string exitInfo)
        {
            exitInfo = "";
            try
            {
                var bc = ai.BehaviourCtrl;
                if (bc == null) return null;
                UnityEngine.Vector3 npcPos = default;
                try { npcPos = bc.transform.position; }
                catch { return null; }
                var maps = new[] { roomMap }.Concat(adjacent).Distinct().ToArray();
                var byMap = new Dictionary<int, List<UnityEngine.Vector3>>();
                foreach (var m in maps)
                {
                    var pts = SampleMapPoints(m, 8);
                    if (pts.Count > 0) byMap[m] = pts;
                }
                if (!byMap.ContainsKey(roomMap)) return null;
                List<KeyValuePair<string, LinkRec>> links;
                lock (_links) links = new List<KeyValuePair<string, LinkRec>>(_links);
                if (links.Count == 0) return null;
                string bestName = "";
                int bestMap = -1;
                float bestD = float.MaxValue;
                bool found = false;
                var details = new List<string>();
                foreach (var kv in links)
                {
                    var link = kv.Value;
                    if (!link.HasStart && !link.HasEnd) continue;
                    int sm = link.HasStart ? NearestMapOf(link.Start, byMap, -1) : -1;
                    int em = link.HasEnd ? NearestMapOf(link.End, byMap, -1) : roomMap;
                    if (sm != roomMap && em != roomMap) continue;
                    int other = sm == roomMap ? em : sm;
                    if (other < 0 || other == roomMap) continue;
                    if (IsPrivateMap(other))
                    {
                        Plugin.Info($"[MoveAway] Door '{link.Name}' leads to private map {other}; skipped.");
                        continue;
                    }
                    UnityEngine.Vector3 door = link.HasStart && sm == roomMap ? link.Start : link.End;
                    float dx = door.x - npcPos.x;
                    float dy = door.y - npcPos.y;
                    float dz = door.z - npcPos.z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (details.Count < 12)
                        details.Add($"'{link.Name}'->{other}(d{(float)Math.Sqrt(d):F0})");
                    if (!found || d < bestD) { bestD = d; bestMap = other; bestName = link.Name; found = true; }
                }
                if (details.Count > 0)
                    Plugin.Info($"[MoveAway] Doors from room {roomMap}: {string.Join(", ", details.ToArray())}.");
                if (!found) return null;
                exitInfo = $"'{bestName}' (d{(float)Math.Sqrt(bestD):F0})";
                return bestMap;
            }
            catch (Exception ex)
            {
                Plugin.Info($"[MoveAway] Exit pairing failed: {ex.Message}");
                return null;
            }
        }

        // Nearest map with readable walk-point positions. -1 when nothing is
        // measurable (caller then picks randomly).
        private static int PickNearestMap(AI ai, int[] pool, out bool measured)
        {
            measured = false;
            try
            {
                var bc = ai.BehaviourCtrl;
                if (bc == null) return -1;
                UnityEngine.Vector3 npcPos = default;
                try { npcPos = bc.transform.position; }
                catch { return -1; }
                var mapMgr = MapManager.Instance;
                if (mapMgr == null) return -1;
                int best = -1;
                float bestD = float.MaxValue;
                foreach (var m in pool)
                {
                    try
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            Manager.MapManager.MapTargetInfo? t = null;
                            try { t = mapMgr.GetUrouroPointByIndex(-1, i, m); }
                            catch { break; }
                            if (t == null) continue;
                            UnityEngine.Vector3 pp = default;
                            try
                            {
                                var pi = t.pInfo;
                                if (pi == null) continue;
                                pp = pi.transform.position;
                            }
                            catch { continue; }
                            float dx = pp.x - npcPos.x;
                            float dy = pp.y - npcPos.y;
                            float dz = pp.z - npcPos.z;
                            float d = dx * dx + dy * dy + dz * dz;
                            if (d < bestD) { bestD = d; best = m; }
                        }
                    }
                    catch { }
                }
                if (best >= 0)
                {
                    measured = true;
                    Plugin.Info($"[MoveAway] Nearest map to NPC is {best} (sqdist {bestD:F0}).");
                    return best;
                }
            }
            catch (Exception ex) { Plugin.Info($"[MoveAway] Nearest-map measuring failed: {ex.Message}"); }
            return -1;
        }

        // Game-authored adjacency: MapInfoParam.NearFloors/TakingFloors for the
        // room's entry in the static mapListTable.
        private static int[] NearbyMaps(int roomMap)
        {
            try
            {
                var table = MapManager.mapListTable;
                if (table == null || !table.ContainsKey(roomMap)) return Array.Empty<int>();
                var info = table[roomMap];
                if (info == null) return Array.Empty<int>();
                var ids = new List<int>();
                try
                {
                    var near = info.NearFloors;
                    if (near != null) foreach (var m in near) ids.Add(m);
                }
                catch { }
                try
                {
                    var taking = info.TakingFloors;
                    if (taking != null) foreach (var m in taking) ids.Add(m);
                }
                catch { }
                var result = ids.Distinct().ToArray();
                if (result.Length > 0)
                    Plugin.Info($"[MoveAway] Adjacent maps to {roomMap}: {string.Join(",", result)}.");
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Info($"[MoveAway] NearbyMaps failed: {ex.Message}");
                return Array.Empty<int>();
            }
        }

        // Public-map filter: excludes private rooms so a "leave the room" order
        // never lands inside another private room. 2D maps stay eligible (most
        // custom maps are 2D). Private = IsRoomMap(full rooms) OR Kind == 1.
        internal static bool IsPrivateMap(int m)
        {
            try { if (MapManager.IsRoomMap(m)) return true; }
            catch { }
            try
            {
                var table = MapManager.mapListTable;
                if (table != null && table.ContainsKey(m))
                {
                    var info = table[m];
                    var k = info?.GetType().GetProperty("Kind")?.GetValue(info, null);
                    if (k != null && Convert.ToInt32(k) == 1) return true;
                }
            }
            catch { }
            return false;
        }

        private static int[] FilterPublicMaps(int[] maps, string what)
        {
            var list = new List<int>();
            var priv = new List<int>();
            try
            {
                foreach (var m in maps)
                {
                    if (IsPrivateMap(m)) { priv.Add(m); continue; }
                    list.Add(m);
                }
            }
            catch { return maps; }
            if (priv.Count > 0)
                Plugin.Info($"[MoveAway] Excluded private maps from {what}: [{string.Join(",", priv)}].");
            else
                Plugin.Info($"[MoveAway] No private maps among {what} [{string.Join(",", maps)}].");
            return list.ToArray();
        }

        private static bool MoveOne(MapManager mapMgr, AI ai, int roomMap)
        {
            var bc = ai.BehaviourCtrl;
            if (bc == null)
            {
                Plugin.Log.LogWarning("[MoveAway] NPC has no BehaviourController; skipping.");
                return false;
            }

            int dest = ResolveDest(ai, roomMap, out string how);
            if (dest < 0)
            {
                Plugin.Log.LogWarning("[MoveAway] No destination available; skipping.");
                return false;
            }
            Plugin.Info($"[MoveAway] NPC leaves map {roomMap} {how}.");

            // Primary: let the manager resolve a proper walk point itself
            // (correct point-type filtering, same as vanilla travel orders).
            try
            {
                MapManager.UroUroPointMove(bc, dest, -1, null);
                QueueMoveVerification(ai, roomMap, dest);
                Plugin.Info($"[MoveAway] NPC move requested: map {roomMap} -> {dest} (manager-resolved; verifying).");
                return true;
            }
            catch (Exception ex) { Plugin.Info($"[MoveAway] UroUroPointMove failed, trying point scan: {ex.GetType().Name}: {ex.Message}"); }

            // Fallback: point lookup WITHOUT transform/job lists — plain ints
            // (the list-based GetUrouroPoint NullRefs on this build when handed
            // nulls). Scan point slots for the first valid one.
            Manager.MapManager.MapTargetInfo? target = null;
            int usedIndex = -1;
            foreach (var alt in new[] { dest }.Concat(AllMapIds()).Concat(ActionVars.CandidateMaps).Distinct())
            {
                if (alt == roomMap) continue;
                try
                {
                    for (int i = 0; i < 64; i++)
                    {
                        Manager.MapManager.MapTargetInfo? cand = null;
                        try { cand = mapMgr.GetUrouroPointByIndex(-1, i, alt); }
                        catch (Exception ex)
                        {
                            Plugin.Info($"[MoveAway] GetUrouroPointByIndex({alt},{i}) threw, next map: {ex.GetType().Name}");
                            break;
                        }
                        if (cand == null) continue;
                        try
                        {
                            if (cand.pInfo == null) continue;
                        }
                        catch { continue; }
                        target = cand;
                        usedIndex = i;
                        dest = alt;
                        break;
                    }
                }
                catch (Exception ex) { Plugin.Info($"[MoveAway] point scan for map {alt} failed: {ex.Message}"); }
                if (target != null) break;
            }

            if (target != null)
            {
                try
                {
                    // Sanity: the point must actually belong to the destination map.
                    int gotMap = -1;
                    try { gotMap = target.map; } catch { }
                    if (gotMap != dest)
                    {
                        Plugin.Log.LogWarning($"[MoveAway] Point map mismatch (want {dest}, got {gotMap}); not sending NPC there.");
                        return false;
                    }
                    // Vanilla point shape for travel orders (TakingAction):
                    // type = 0, job = -1 before SetCharaMapMove.
                    try { target.type = 0; } catch { }
                    try { target.job = -1; } catch { }
                    MapManager.SetCharaMapMove(0, bc, target, false);
                    QueueMoveVerification(ai, roomMap, dest);
                    Plugin.Info($"[MoveAway] NPC move requested: map {roomMap} -> {dest} (point {usedIndex}; verifying).");
                    return true;
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[MoveAway] SetCharaMapMove failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            else
            {
                Plugin.Log.LogWarning($"[MoveAway] No urouro point found for map {dest} or alternatives; NPC stays.");
            }
            return false;
        }

        private static IEnumerable<int> AllMapIds()
        {
            List<int> ids = new();
            try
            {
                var all = MapManager.Instance?.allMaps;
                if (all != null)
                    foreach (var m in all)
                        ids.Add(m);
            }
            catch { }
            return ids;
        }
    }
}
