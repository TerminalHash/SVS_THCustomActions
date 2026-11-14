#nullable enable
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace SVS_ADVScenarioSwapper
{
    // NOTE: no [BepInProcess] on purpose — the game exe name varies by build,
    // and a wrong name would silently prevent loading.
    [BepInPlugin(GUID, PLUGIN_NAME, VERSION)]
    [BepInDependency(AdvLoaderGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BasePlugin
    {
        public const string PLUGIN_NAME = "SVS_ADVScenarioSwapper";
        public const string COPYRIGHT = "Copyright © TerminalHash 2026";
        public const string COMPANY = "SVS_ADVScenarioSwapper";

        public const string GUID = "th.SVS.ADVScenarioSwapper";
        public const string VERSION = "1.0.0";

        // The swap is useless without ADVLoader's tag system, so the
        // dependency is hard. The calls stay reflection-based only to keep the
        // build independent of the ADVLoader assembly.
        public const string AdvLoaderGuid = "DS27.SVS.ADVLoader";

        internal static new ManualLogSource Log = null!;

        // configuration entries
        public static ConfigEntry<bool> VerboseLog = null!;
        public static ConfigEntry<bool> UseTraitSuffix = null!;

        public override void Load()
        {
            Log = base.Log;

            VerboseLog = Config.Bind("Logging", "Debug log", false,
                "Toggle additional lines for debug purpose.");
            UseTraitSuffix = Config.Bind("Behavior", "Use scenarios with suffix _traitxxx", false,
                "For quick tests of unique dialogue lines or using non-hardcoded trait lines");

            try { Hooks.PatchAll(); }
            catch (Exception ex) { Log.LogWarning($"[Hooks] Setup failed: {ex}"); }

            Info($"{PLUGIN_NAME} v{VERSION} loaded.");
        }

        public override bool Unload()
        {
            Hooks.Unpatch();
            return true;
        }

        internal static void Info(string msg)
        {
            try { if (VerboseLog.Value) Log.LogInfo(msg); } catch { }
        }
    }
}
