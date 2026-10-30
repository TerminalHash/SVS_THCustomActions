#nullable enable
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using SVS_MAHelpers.Actions.Clothing;
using SVS_MAHelpers.Actions.Gossip;
using SVS_MAHelpers.Actions.IgnoreMe;
using SVS_MAHelpers.Actions.MoveAway;
using SVS_MAHelpers.Actions.StopHarassing;

namespace SVS_MAHelpers
{
    // NOTE: no [BepInProcess] on purpose — the game exe name varies by build,
    // and a wrong name would silently prevent loading.
    [BepInPlugin(GUID, PLUGIN_NAME, VERSION)]
    [BepInDependency(ActionVars.MoreActionsGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BasePlugin
    {
        public const string PLUGIN_NAME = "SVS_MAHelpers";
        public const string COPYRIGHT = "Copyright ©  2026";
        public const string COMPANY = "SVS_MAHelpers";

        public const string GUID = "org.svs.mahelpers";
        public const string VERSION = "2.0.0";

        internal static new ManualLogSource Log = null!;

        public static ConfigEntry<bool> VerboseLog = null!;

        // Debug-only, specified by the IgnoreMe logic doc: starting a talk with
        // the ignoring NPC retracts the record (talks run untouched either way).
        public static ConfigEntry<bool> ClearOnTalkStart = null!;

        public override void Load()
        {
            Log = base.Log;

            VerboseLog = Config.Bind("Logging", "VerboseLog", false, "File-only info logs (off by default; nothing is shown on screen).");
            ClearOnTalkStart = Config.Bind("Behavior", "ClearOnTalkStart", false,
                "Debug-only: when true, starting a talk with the ignoring NPC retracts the ignore record (talks run untouched either way). Off by default.");

            try { Hooks.PatchAll(); }
            catch (Exception ex) { Log.LogWarning($"[Hooks] Setup failed: {ex}"); }

            try { RegisterTriggers(); }
            catch (Exception ex) { Log.LogWarning($"[Trigger] Registration failed: {ex}"); }

            // Per-frame enforcers: clothing strip re-apply, IgnoreMe H-watch.
            try
            {
                TriggerService.RegisterTick(ClothingService.EnforceActive);
                TriggerService.RegisterTick(IgnoreService.EnforceActive);
            }
            catch (Exception ex) { Log.LogWarning($"[Trigger] Tick registration failed: {ex}"); }

            Info($"{PLUGIN_NAME} v{VERSION} loaded. Actions: 503/504/505/506/507/508/509 (502 reserved).");
        }

        private static void RegisterTriggers()
        {
            TriggerService.RegisterTrigger(ActionVars.MoveAwayActionNo, ActionVars.MoveAwayAdvName,
                (charas, reason, ask) => MoveAwayService.Schedule(charas, reason, ask));

            TriggerService.RegisterTrigger(ActionVars.IgnoreActionNo, ActionVars.IgnoreAdvName,
                (charas, reason, ask) => IgnoreService.Schedule(charas, reason, ask));

            TriggerService.RegisterTrigger(ActionVars.HarassActionNo, ActionVars.HarassAdvName,
                (charas, reason, ask) => HarassService.Schedule(charas, reason, ask));

            TriggerService.RegisterTrigger(ActionVars.BlackmailUnderwearActionNo, ActionVars.BlackmailUnderwearAdvName,
                (charas, reason, ask) => ClothingService.ScheduleUnderwear(charas, reason, ActionVars.BlackmailUnderwearActionNo, ask));

            TriggerService.RegisterTrigger(ActionVars.NakedActionNo, ActionVars.NakedAdvName,
                (charas, reason, ask) => ClothingService.ScheduleNaked(charas, reason, ActionVars.NakedActionNo, ask));

            TriggerService.RegisterTrigger(ActionVars.GossipActionNo, ActionVars.GossipAdvName,
                (charas, reason, ask) => GossipService.Schedule(charas, reason, ask));

            TriggerService.RegisterTrigger(ActionVars.LoveUnderwearActionNo, ActionVars.LoveUnderwearAdvName,
                (charas, reason, ask) => ClothingService.ScheduleUnderwear(charas, reason, ActionVars.LoveUnderwearActionNo, ask));

            // 502 Have3P is reserved (Actions/Have3P) and intentionally not
            // registered in this iteration.
        }

        public override bool Unload()
        {
            Hooks.Unpatch();
            return true;
        }

        // Stolen-panties ledger public surface, kept on the plugin type for
        // consumers that reference SVS_MAHelpers.Plugin (previous merged build).
        public static event Action<int, string, int>? Stolen
        {
            add => PantiesLedger.Stolen += value;
            remove => PantiesLedger.Stolen -= value;
        }

        public static int GetCount(int npcIdx) => PantiesLedger.GetCount(npcIdx);
        public static int Total => PantiesLedger.Total;
        public static string GetName(int npcIdx) => PantiesLedger.GetName(npcIdx);
        public static bool HasHold(int npcIdx, int today) => PantiesLedger.HasHold(npcIdx, today);

        internal static void Info(string msg)
        {
            try { if (VerboseLog.Value) Log.LogInfo(msg); } catch { }
        }
    }
}
