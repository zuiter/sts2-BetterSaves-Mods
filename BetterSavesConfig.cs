using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace BetterSaves;

internal enum SyncMode
{
    SaveOnly = 0,
    FullSync = 1,
    DataOnly = 2
}

internal sealed class BetterSavesConfigData
{
    public SyncMode SyncMode { get; set; } = SyncMode.FullSync;
    public int LastVanillaProfileId { get; set; }
    public int LastModdedProfileId { get; set; }
    public Dictionary<string, ulong> ModdedMultiplayerLocalPlayerIds { get; set; } = new();
}

internal static class BetterSavesConfig
{
    private static readonly object ConfigLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static BetterSavesConfigData? _cached;
    private static bool _createdThisSession;

    public static SyncMode CurrentMode
    {
        get
        {
            lock (ConfigLock)
            {
                _cached ??= LoadUnsafe();
                return _cached.SyncMode;
            }
        }
    }

    public static bool IsFullSyncEnabled => CurrentMode == SyncMode.FullSync;
    public static bool IsSaveSyncEnabled => CurrentMode is SyncMode.SaveOnly or SyncMode.FullSync;
    public static bool IsDataSyncEnabled => CurrentMode is SyncMode.DataOnly or SyncMode.FullSync;
    public static bool IsSettingsSyncEnabled => CurrentMode == SyncMode.FullSync;
    public static bool UsesSharedProfileSelection => CurrentMode != SyncMode.DataOnly;
    public static bool IsFreshInstallSession => _createdThisSession;

    public static int GetPreferredProfileId(bool vanillaMode)
    {
        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();
            return vanillaMode
                ? _cached.LastVanillaProfileId
                : _cached.LastModdedProfileId;
        }
    }

    public static void SetMode(SyncMode mode)
    {
        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();
            if (_cached.SyncMode == mode)
            {
                return;
            }

            _cached.SyncMode = mode;
            SaveUnsafe(_cached);
        }

        SaveInteropService.ReconcileNow(
            $"config change: {mode}",
            VanillaModeCompatibilityPatches.StartupReconcilePreference);
    }

    public static void SetPreferredProfileId(bool vanillaMode, int profileId)
    {
        if (profileId is < 1 or > 3)
        {
            return;
        }

        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();

            if (vanillaMode)
            {
                if (_cached.LastVanillaProfileId == profileId)
                {
                    return;
                }

                _cached.LastVanillaProfileId = profileId;
            }
            else
            {
                if (_cached.LastModdedProfileId == profileId)
                {
                    return;
                }

                _cached.LastModdedProfileId = profileId;
            }

            SaveUnsafe(_cached);
        }
    }

    public static ulong GetModdedMultiplayerLocalPlayerId(int profileId)
    {
        if (profileId is < 1 or > 3)
        {
            return 0;
        }

        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();
            return _cached.ModdedMultiplayerLocalPlayerIds.TryGetValue(profileId.ToString(), out var value)
                ? value
                : 0;
        }
    }

    public static void SetModdedMultiplayerLocalPlayerId(int profileId, ulong playerId)
    {
        if (profileId is < 1 or > 3 || playerId == 0)
        {
            return;
        }

        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();
            if (_cached.ModdedMultiplayerLocalPlayerIds.TryGetValue(profileId.ToString(), out var existing)
                && existing == playerId)
            {
                return;
            }

            _cached.ModdedMultiplayerLocalPlayerIds[profileId.ToString()] = playerId;
            SaveUnsafe(_cached);
        }
    }

    private static BetterSavesConfigData LoadUnsafe()
    {
        try
        {
            var configPath = GetConfigPath();
            if (!File.Exists(configPath))
            {
                _createdThisSession = true;
                var defaults = new BetterSavesConfigData();
                SaveUnsafe(defaults);
                return defaults;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<BetterSavesConfigData>(json, JsonOptions)
                ?? new BetterSavesConfigData();
            config.ModdedMultiplayerLocalPlayerIds ??= new Dictionary<string, ulong>();
            return config;
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to load config, using defaults: {ex}");
            return new BetterSavesConfigData();
        }
    }

    private static void SaveUnsafe(BetterSavesConfigData config)
    {
        try
        {
            var configPath = GetConfigPath();
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(configPath, json);
            Log.Info($"[BetterSaves] Saved config to '{configPath}' with mode '{config.SyncMode}'.");
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to save config: {ex}");
        }
    }

    private static string GetConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2",
            "mods",
            "BetterSaves",
            "config.json");
    }
}
