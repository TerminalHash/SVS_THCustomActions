#nullable enable
using System;
using System.Reflection;
using SV.Chara;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Small shared reads that do not belong to one action. Best-effort:
    /// unreadable state yields a sentinel instead of throwing.
    /// </summary>
    internal static class Utils
    {
        /// <summary>Current save day, or -1 when unreadable.</summary>
        internal static int TryReadDay()
        {
            try
            {
                var sd = Manager.Game.saveData;
                if (sd == null) return -1;
                return sd.Day;
            }
            catch { return -1; }
        }

        /// <summary>Best-effort label of the current simulation period.</summary>
        internal static string TryReadPeriodLabel()
        {
            try
            {
                object? sim = null;
                try { sim = Manager.SimulationManager.Instance; } catch { }
                if (sim == null) return "unknown-period";
                try
                {
                    var modeProp = sim.GetType().GetProperty("mode");
                    object? modeVal = null;
                    try { modeVal = modeProp?.GetValue(sim, null); } catch { }
                    if (modeVal != null) return "mode-" + modeVal;
                }
                catch { }
                return "unknown-period";
            }
            catch { return "unknown-period"; }
        }

        private static PropertyInfo? _paramProp;
        private static PropertyInfo? _indexProp;

        /// <summary>
        /// charasGameParam.Index off a SaveData.Actor (or anything exposing it),
        /// resolved reflectively once and cached.
        /// </summary>
        internal static int ReadActorIndex(object actorObj)
        {
            var t = actorObj.GetType();
            var pp = _paramProp;
            if (pp == null || pp.DeclaringType == null || !pp.DeclaringType.IsAssignableFrom(t))
            {
                pp = t.GetProperty("charasGameParam");
                if (pp == null) throw new MissingMemberException("charasGameParam");
                _paramProp = pp;
            }
            object? param = pp.GetValue(actorObj, null);
            if (param == null) throw new NullReferenceException("charasGameParam");
            var ip = _indexProp;
            if (ip == null || ip.DeclaringType == null || !ip.DeclaringType.IsAssignableFrom(param.GetType()))
            {
                ip = param.GetType().GetProperty("Index");
                if (ip == null) throw new MissingMemberException("Index");
                _indexProp = ip;
            }
            return Convert.ToInt32(ip.GetValue(param, null));
        }

        /// <summary>True when the NPC shares any live TalkManager task with the PC.</summary>
        internal static bool SharesActiveTalkWith(AI npcAI, int pcIdx)
        {
            try
            {
                int npcIdx = npcAI.charaData.charasGameParam.Index;
                var tasks = Manager.TalkManager.Instance?.tasks;
                if (tasks == null) return false;
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    bool hasNpc = false, hasPc = false;
                    try
                    {
                        var charas = t.charas;
                        if (charas == null) continue;
                        foreach (var ai in charas)
                        {
                            if (ai == null) continue;
                            int i;
                            try { i = ai.charaData.charasGameParam.Index; }
                            catch { continue; }
                            if (i == npcIdx) hasNpc = true;
                            if (i == pcIdx) hasPc = true;
                        }
                    }
                    catch { continue; }
                    if (hasNpc && hasPc) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
