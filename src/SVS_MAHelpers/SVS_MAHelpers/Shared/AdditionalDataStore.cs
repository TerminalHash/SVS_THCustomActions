#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Soft bridge to SVS_AdditionalSaveData (GUID DS27.SVS.AdditionalData),
    /// so small mod state lives in its sidecar <c>mod_&lt;save&gt;.json</c> and
    /// the main save file is never touched. Everything is reflection and
    /// optional: without the plugin every call reports unavailable and the
    /// caller keeps its session-only behaviour.
    /// <para>
    /// Storage is the plugin's global string map: one value per category/key.
    /// Failed lookups are retried at most every few seconds, which keeps a
    /// missing plugin from rescanning the assembly list on every query.
    /// </para>
    /// </summary>
    internal static class AdditionalDataStore
    {
        // BepInEx GUID of SVS_AdditionalSaveData (verified in the shipped DLL).
        internal const string PluginGuid = "DS27.SVS.AdditionalData";

        private const string TypeName = "AdditionalSaveData.AdditionalData";
        private static readonly object _gate = new();
        private static Type? _type;
        private static MethodInfo? _add;
        private static MethodInfo? _get;
        private static MethodInfo? _remove;
        private static MethodInfo? _keys;
        private static object? _stringType;
        private static DateTime _retryUtc;

        internal static bool Available => Resolve() != null;

        internal static bool TrySetString(string category, string key, string value)
        {
            if (Resolve() == null || _add == null) return false;
            try
            {
                _add.Invoke(null, new object?[] { category, key, value, _stringType });
                return true;
            }
            catch { return false; }
        }

        internal static string GetString(string category, string key)
        {
            if (Resolve() == null || _get == null) return "";
            try { return _get.Invoke(null, new object?[] { category, key }) as string ?? ""; }
            catch { return ""; }
        }

        internal static void RemoveValue(string category, string key)
        {
            if (Resolve() == null || _remove == null) return;
            try { _remove.Invoke(null, new object?[] { category, key, _stringType }); }
            catch { }
        }

        internal static List<string> GetKeys(string category)
        {
            if (Resolve() == null || _keys == null) return new List<string>();
            try
            {
                return _keys.Invoke(null, new object?[] { category, _stringType })
                    as List<string> ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        private static Type? Resolve()
        {
            lock (_gate)
            {
                if (_type != null) return _type;
                if (DateTime.UtcNow < _retryUtc) return null;
                _retryUtc = DateTime.UtcNow.AddSeconds(5);

                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = asm.GetType(TypeName, false);
                        if (type == null) continue;

                        var enumType = type.GetNestedType("DataType", BindingFlags.Public);
                        var add = type.GetMethod("AddGlobalData", BindingFlags.Public | BindingFlags.Static);
                        var get = type.GetMethod("GetGlobalDataString", BindingFlags.Public | BindingFlags.Static);
                        var keys = type.GetMethod("GetGlobalKeyNamesFromCategory", BindingFlags.Public | BindingFlags.Static)
                            ?? type.GetMethod("GetGlobalKeyNamesFromCategories", BindingFlags.Public | BindingFlags.Static);
                        if (enumType == null || add == null || get == null || keys == null) continue;

                        _stringType = Enum.Parse(enumType, "StringValue");
                        _add = add;
                        _get = get;
                        _keys = keys;
                        _remove = type.GetMethod("RemoveGlobalDataValue", BindingFlags.Public | BindingFlags.Static);
                        _type = type;
                        return _type;
                    }
                }
                catch { }
                return null;
            }
        }
    }
}
