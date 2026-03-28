using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BetterSaves;

[ModInitializer("Init")]
public static class Entry
{
    private static readonly object InitLock = new();
    private static Harmony? _harmony;
    private static bool _initialized;

    public static void Init()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }

        _harmony = new Harmony("BetterSaves");
        _harmony.PatchAll();
        VanillaModeCompatibilityPatches.Apply(_harmony);

        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        SaveInteropService.Initialize();

        Log.Debug("[BetterSaves] Save interop initialized.");
    }
}
