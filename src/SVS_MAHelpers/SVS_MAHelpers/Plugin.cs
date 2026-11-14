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
    [BepInDependency(ScenarioSwapperGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(AdditionalDataStore.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string PLUGIN_NAME = "SVS_MAHelpers";
        public const string COPYRIGHT = "Copyright © TerminalHash 2026";
        public const string COMPANY = "SVS_MAHelpers";

        public const string GUID = "th.SVS.MAHelpers";
        public const string VERSION = "2.1.0";

        // The universal scenario-variant feature is shipped/installed with the
        // helpers; it in turn hard-requires ADVLoader.
        public const string ScenarioSwapperGuid = "th.SVS.ADVScenarioSwapper";

        internal static new ManualLogSource Log = null!;

        // configuration entries
        public static ConfigEntry<bool> VerboseLog = null!;
        public static ConfigEntry<bool> ClearOnTalkStart = null!;

        public override void Load()
        {
            Log = base.Log;

            VerboseLog = Config.Bind("Logging", "Debug log", false, "Toggle additional lines for debug purpose.");
            ClearOnTalkStart = Config.Bind("Behavior", "Clear on dialogue start", false,
                "Debug-only: when true, starting a talk with the ignoring NPC retracts the ignore record.");

            try { Hooks.PatchAll(); }
            catch (Exception ex) { Log.LogWarning($"[Hooks] Setup failed: {ex}"); }

            try { RegisterTriggers(); }
            catch (Exception ex) { Log.LogWarning($"[Trigger] Registration failed: {ex}"); }

            // Per-frame enforcers: clothing strip re-apply, IgnoreMe H-watch,
            // MoveAway deferred move verification.
            try
            {
                TriggerService.RegisterTick(ClothingService.EnforceActive);
                TriggerService.RegisterTick(IgnoreService.EnforceActive);
                TriggerService.RegisterTick(MoveAwayService.EnforceMoves);
            }
            catch (Exception ex) { Log.LogWarning($"[Trigger] Tick registration failed: {ex}"); }

            // A different save object in the same process must not reuse the
            // previous save's strips, holds, ignores, maps or asks.
            try
            {
                SaveGeneration.Register(ClothingService.ResetState);
                SaveGeneration.Register(PantiesLedger.Reset);
                SaveGeneration.Register(IgnoreService.ResetState);
                SaveGeneration.Register(MoveAwayService.ResetState);
                SaveGeneration.Register(HarassService.Reset);
                SaveGeneration.Register(TriggerService.ResetAsks);
            }
            catch (Exception ex) { Log.LogWarning($"[Save] Reset registration failed: {ex}"); }

            Info($"{PLUGIN_NAME} v{VERSION} loaded. Actions: 503/504/505/506/507/508/509/510");
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

            TriggerService.RegisterTrigger(ActionVars.ReturnPantiesActionNo, ActionVars.ReturnPantiesAdvName,
                (charas, reason, ask) => ClothingService.ScheduleReturn(charas, reason, ask));

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
