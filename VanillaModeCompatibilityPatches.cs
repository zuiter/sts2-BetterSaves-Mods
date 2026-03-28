using Godot;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BetterSaves;

internal static class VanillaModeCompatibilityPatches
{
    private const string BetterSavesId = "BetterSaves";
    private const string ModManagerTypeName = "MegaCrit.Sts2.Core.Modding.ModManager";
    private const string SaveManagerTypeName = "MegaCrit.Sts2.Core.Saves.SaveManager";
    private const string UserDataPathProviderTypeName = "MegaCrit.Sts2.Core.Saves.UserDataPathProvider";
    private const string SettingsScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen";
    private const string PaginatorTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NPaginator";
    private const string PaginateArrowTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NPaginateArrow";
    private const string ProfileButtonTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen.NProfileButton";
    private static readonly string[] ImmediateReconcileSaveMethods =
    [
        "SavePrefsFile",
        "SaveProfile",
        "SaveProgressFile",
        "SaveRunHistory",
        "SaveSettings",
        "EndSaveBatch",
        "DeleteCurrentRun",
        "DeleteCurrentMultiplayerRun"
    ];

    private static readonly object StateLock = new();

    private static bool? _compatibilityMode;
    private static bool _applied;
    private static bool _loggedDetection;
    private static bool _loggedForcedModdedFalse;

    public static bool IsCompatibilityModeEnabled
    {
        get
        {
            lock (StateLock)
            {
                return _compatibilityMode == true;
            }
        }
    }

    public static SaveInteropService.ReconcilePreference CurrentReconcilePreference =>
        ShouldForceVanillaMode()
            ? SaveInteropService.ReconcilePreference.VanillaToModded
            : SaveInteropService.ReconcilePreference.ModdedToVanilla;

    public static SaveInteropService.ReconcilePreference StartupReconcilePreference =>
        ShouldForceVanillaMode()
            ? SaveInteropService.ReconcilePreference.VanillaToModded
            : SaveInteropService.ReconcilePreference.Auto;

    public static void Apply(Harmony harmony)
    {
        lock (StateLock)
        {
            if (_applied)
            {
                return;
            }

            _applied = true;
        }

        PatchMethodsByName(
            harmony,
            ModManagerTypeName,
            "GetGameplayRelevantModNameList",
            nameof(GetGameplayRelevantModNameListPostfix));

        PatchMethodsByName(
            harmony,
            ModManagerTypeName,
            "IsRunningModded",
            nameof(IsRunningModdedPostfix));

        PatchMethodsByName(
            harmony,
            ModManagerTypeName,
            "GetLoadedMods",
            nameof(GetLoadedModsPostfix));

        PatchPropertyGetter(
            harmony,
            ModManagerTypeName,
            "Mods",
            nameof(ModsGetterPostfix));

        PatchMethodsByName(
            harmony,
            SaveManagerTypeName,
            "GetProfileScopedPath",
            nameof(ProfileScopedPathPostfix));

        PatchMethodsByName(
            harmony,
            UserDataPathProviderTypeName,
            "GetProfileScopedBasePath",
            nameof(ProfileScopedPathPostfix));

        PatchMethodsByName(
            harmony,
            UserDataPathProviderTypeName,
            "GetProfileScopedPath",
            nameof(ProfileScopedPathPostfix));

        foreach (var methodName in ImmediateReconcileSaveMethods)
        {
            PatchMethodsByName(
                harmony,
                SaveManagerTypeName,
                methodName,
                nameof(ImmediateReconcilePostfix));
        }

        PatchMethodsByName(
            harmony,
            SaveManagerTypeName,
            "SaveRun",
            nameof(ImmediateReconcileTaskPostfix));

        PatchMethodsByName(
            harmony,
            SaveManagerTypeName,
            "SyncCloudToLocal",
            nameof(ImmediateReconcileTaskPostfix));

        PatchMethodsByName(
            harmony,
            SettingsScreenTypeName,
            "_Ready",
            nameof(SettingsScreenReadyPostfix));

        PatchMethodsByName(
            harmony,
            ProfileButtonTypeName,
            "_Ready",
            nameof(ProfileButtonReadyPostfix));

        PatchMethodsByName(
            harmony,
            PaginatorTypeName,
            "OnIndexChanged",
            nameof(PaginatorIndexChangedPostfix));

        PatchMethodsByNameWithPrefix(
            harmony,
            PaginateArrowTypeName,
            "OnRelease",
            nameof(PaginateArrowOnReleasePrefix));
    }

    private static void PatchMethodsByName(
        Harmony harmony,
        string typeName,
        string methodName,
        string postfixName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            Log.Info($"[BetterSaves] Could not find type '{typeName}' for patch '{methodName}'.");
            return;
        }

        var methods = AccessTools.GetDeclaredMethods(type)
            .Where(method => method.Name == methodName)
            .ToList();

        if (methods.Count == 0)
        {
            Log.Info($"[BetterSaves] Could not find method '{typeName}.{methodName}'.");
            return;
        }

        var postfix = new HarmonyMethod(
            AccessTools.DeclaredMethod(typeof(VanillaModeCompatibilityPatches), postfixName));

        foreach (var method in methods)
        {
            harmony.Patch(method, postfix: postfix);
            Log.Info(
                $"[BetterSaves] Patched '{method.DeclaringType?.FullName}.{method.Name}' " +
                $"returning '{method.ReturnType.FullName}'.");
        }
    }

    private static void PatchMethodsByNameWithPrefix(
        Harmony harmony,
        string typeName,
        string methodName,
        string prefixName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            Log.Info($"[BetterSaves] Could not find type '{typeName}' for patch '{methodName}'.");
            return;
        }

        var methods = AccessTools.GetDeclaredMethods(type)
            .Where(method => method.Name == methodName)
            .ToList();

        if (methods.Count == 0)
        {
            Log.Info($"[BetterSaves] Could not find method '{typeName}.{methodName}'.");
            return;
        }

        var prefix = new HarmonyMethod(
            AccessTools.DeclaredMethod(typeof(VanillaModeCompatibilityPatches), prefixName));

        foreach (var method in methods)
        {
            harmony.Patch(method, prefix: prefix);
            Log.Info(
                $"[BetterSaves] Patched '{method.DeclaringType?.FullName}.{method.Name}' " +
                $"with prefix returning '{method.ReturnType.FullName}'.");
        }
    }

    private static void PatchPropertyGetter(
        Harmony harmony,
        string typeName,
        string propertyName,
        string postfixName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            Log.Info($"[BetterSaves] Could not find type '{typeName}' for property patch '{propertyName}'.");
            return;
        }

        var getter = AccessTools.PropertyGetter(type, propertyName);
        if (getter is null)
        {
            Log.Info($"[BetterSaves] Could not find property getter '{typeName}.{propertyName}'.");
            return;
        }

        var postfix = new HarmonyMethod(
            AccessTools.DeclaredMethod(typeof(VanillaModeCompatibilityPatches), postfixName));
        harmony.Patch(getter, postfix: postfix);
        Log.Info(
            $"[BetterSaves] Patched property getter '{type.FullName}.{propertyName}' " +
            $"returning '{getter.ReturnType.FullName}'.");
    }

    private static void GetGameplayRelevantModNameListPostfix(ref List<string>? __result)
    {
        if (__result is null)
        {
            return;
        }

        var hadBetterSaves = __result.Any(IsBetterSavesId);
        var filtered = __result
            .Where(modId => !IsBetterSavesId(modId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        __result = filtered;

        var shouldForceVanillaMode = filtered.Count == 0 && (hadBetterSaves || DetectOnlyBetterSavesLoaded());
        SetCompatibilityMode(shouldForceVanillaMode, "GetGameplayRelevantModNameList");
    }

    private static void IsRunningModdedPostfix(ref bool __result)
    {
        if (!ShouldForceVanillaMode())
        {
            return;
        }

        if (__result && !_loggedForcedModdedFalse)
        {
            _loggedForcedModdedFalse = true;
            Log.Info("[BetterSaves] Forcing IsRunningModded=false because BetterSaves is the only active mod.");
        }

        __result = false;
    }

    private static void GetLoadedModsPostfix(ref IEnumerable<Mod>? __result)
    {
        if (__result is null || !ShouldForceVanillaMode())
        {
            return;
        }

        __result = FilterMods(__result).ToList();
    }

    private static void ModsGetterPostfix(ref IReadOnlyList<Mod>? __result)
    {
        if (__result is null || !ShouldForceVanillaMode())
        {
            return;
        }

        __result = FilterMods(__result).ToList();
    }

    private static void ProfileScopedPathPostfix(ref string? __result)
    {
        if (!ShouldForceVanillaMode() || string.IsNullOrEmpty(__result))
        {
            return;
        }

        var normalized = NormalizeProfileScopedPath(__result);
        if (normalized == __result)
        {
            return;
        }

        Log.Info($"[BetterSaves] Rewriting profile-scoped path '{__result}' to '{normalized}'.");
        __result = normalized;
    }

    private static void ImmediateReconcilePostfix(MethodBase __originalMethod)
    {
        SaveInteropService.ReconcileNow(
            $"save hook: {__originalMethod.Name}",
            GetCurrentReconcilePreference());
    }

    private static void ImmediateReconcileTaskPostfix(ref Task? __result, MethodBase __originalMethod)
    {
        if (__result is null)
        {
            SaveInteropService.ReconcileNow(
                $"save hook: {__originalMethod.Name}",
                GetCurrentReconcilePreference());
            return;
        }

        __result = WrapTaskWithReconcile(
            __result,
            __originalMethod.Name);
    }

    private static async Task WrapTaskWithReconcile(
        Task originalTask,
        string methodName)
    {
        try
        {
            await originalTask.ConfigureAwait(false);
        }
        finally
        {
            SaveInteropService.ReconcileNow(
                $"save hook: {methodName}",
                GetTaskReconcilePreference(methodName));
        }
    }

    private static SaveInteropService.ReconcilePreference GetCurrentReconcilePreference()
    {
        return CurrentReconcilePreference;
    }

    private static SaveInteropService.ReconcilePreference GetTaskReconcilePreference(string methodName)
    {
        return string.Equals(methodName, "SyncCloudToLocal", StringComparison.Ordinal)
            ? StartupReconcilePreference
            : GetCurrentReconcilePreference();
    }

    private static void SettingsScreenReadyPostfix(object __instance)
    {
        if (__instance is Node node)
        {
            ModdingScreenSettingsUi.InstallInSettingsScreen(node);
        }
    }

    private static void ProfileButtonReadyPostfix(object __instance)
    {
        if (__instance is Node node)
        {
            ProfileScreenSaveTypeUi.InstallInProfileButton(node);
        }
    }

    private static void PaginatorIndexChangedPostfix(object __instance)
    {
        ModdingScreenSettingsUi.HandleNativePaginatorIndexChanged(__instance);
    }

    private static bool PaginateArrowOnReleasePrefix(object __instance)
    {
        return !ModdingScreenSettingsUi.HandleNativePaginateArrowRelease(__instance);
    }

    private static string NormalizeProfileScopedPath(string path)
    {
        var normalized = path
            .Replace("/modded/profile", "/profile", StringComparison.OrdinalIgnoreCase)
            .Replace("\\modded\\profile", "\\profile", StringComparison.OrdinalIgnoreCase);

        return normalized;
    }

    private static bool ShouldForceVanillaMode()
    {
        lock (StateLock)
        {
            if (_compatibilityMode.HasValue)
            {
                return _compatibilityMode.Value;
            }
        }

        var detected = DetectOnlyBetterSavesLoaded();
        SetCompatibilityMode(detected, "assembly scan");
        return detected;
    }

    private static void SetCompatibilityMode(bool enabled, string source)
    {
        lock (StateLock)
        {
            _compatibilityMode = enabled;
        }

        Log.Info($"[BetterSaves] Vanilla compatibility mode = {enabled} ({source}).");
    }

    private static IEnumerable<Mod> FilterMods(IEnumerable<Mod> mods)
    {
        return mods.Where(ShouldKeepMod).ToList();
    }

    private static bool DetectOnlyBetterSavesLoaded()
    {
        var modsRoot = GetModsRoot();
        if (modsRoot is null)
        {
            return false;
        }

        var loadedModIds = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => TryGetModIdFromAssembly(assembly, modsRoot))
            .Where(modId => !string.IsNullOrWhiteSpace(modId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(modId => modId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasBetterSaves = loadedModIds.Any(IsBetterSavesId);
        var hasOtherMods = loadedModIds.Any(modId => !IsBetterSavesId(modId));
        var detected = hasBetterSaves && !hasOtherMods;

        if (!_loggedDetection)
        {
            _loggedDetection = true;
            var modsText = loadedModIds.Count == 0 ? "<none>" : string.Join(", ", loadedModIds);
            Log.Info(
                $"[BetterSaves] Loaded mod assemblies: {modsText}. " +
                $"Only BetterSaves active: {detected}.");
        }

        return detected;
    }

    private static string? GetModsRoot()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(Entry).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            return null;
        }

        return Directory.GetParent(assemblyDirectory)?.FullName;
    }

    private static string? TryGetModIdFromAssembly(Assembly assembly, string modsRoot)
    {
        string location;
        try
        {
            location = assembly.Location;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(location);
        }
        catch
        {
            return null;
        }

        if (!fullPath.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var assemblyDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            return null;
        }

        var modDirectory = new DirectoryInfo(assemblyDirectory).Name;
        if (string.Equals(modDirectory, "mods", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return modDirectory;
    }

    private static bool IsBetterSavesId(string? modId)
    {
        return string.Equals(modId, BetterSavesId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldKeepMod(Mod mod)
    {
        return !IsBetterSavesId(TryGetModId(mod));
    }

    private static string? TryGetModId(Mod mod)
    {
        var idProperty = mod.GetType().GetProperty("Id");
        if (idProperty?.GetValue(mod) is string idValue)
        {
            return idValue;
        }

        var manifestProperty = mod.GetType().GetProperty("Manifest");
        var manifest = manifestProperty?.GetValue(mod);
        if (manifest is not null)
        {
            var manifestIdProperty = manifest.GetType().GetProperty("Id");
            if (manifestIdProperty?.GetValue(manifest) is string manifestId)
            {
                return manifestId;
            }
        }

        return null;
    }
}
