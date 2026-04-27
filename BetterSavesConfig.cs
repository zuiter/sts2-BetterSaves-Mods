using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace BetterSaves;

internal enum SyncMode
{
    SaveOnly = 0,
    FullSync = 1,
    DataOnly = 2
}

internal enum FirstSyncBootstrapState
{
    Resolved = 0,
    Pending = 1,
    Conflict = 2
}

internal sealed class BetterSavesConfigData
{
    public SyncMode SyncMode { get; set; } = SyncMode.FullSync;
    public FirstSyncBootstrapState BootstrapState { get; set; } = FirstSyncBootstrapState.Resolved;
    public bool BootstrapBackupCreated { get; set; }
    public string? BootstrapBackupPath { get; set; }
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
    public static bool IsBootstrapPending => BootstrapState == FirstSyncBootstrapState.Pending;
    public static bool IsBootstrapBackupCreated
    {
        get
        {
            lock (ConfigLock)
            {
                _cached ??= LoadUnsafe();
                return _cached.BootstrapBackupCreated;
            }
        }
    }

    public static string? BootstrapBackupPath
    {
        get
        {
            lock (ConfigLock)
            {
                _cached ??= LoadUnsafe();
                return _cached.BootstrapBackupPath;
            }
        }
    }

    public static FirstSyncBootstrapState BootstrapState
    {
        get
        {
            lock (ConfigLock)
            {
                _cached ??= LoadUnsafe();
                return _cached.BootstrapState;
            }
        }
    }

    public static bool IsBootstrapConflict => BootstrapState == FirstSyncBootstrapState.Conflict;

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

    public static void SetBootstrapState(FirstSyncBootstrapState state, string reason)
    {
        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();
            if (_cached.BootstrapState == state)
            {
                return;
            }

            _cached.BootstrapState = state;
            SaveUnsafe(_cached);
        }

        Log.Info($"[BetterSaves] Updated first-sync bootstrap state to '{state}' ({reason}).");
    }

    public static void MarkBootstrapBackupCreated(string backupPath)
    {
        lock (ConfigLock)
        {
            _cached ??= LoadUnsafe();
            if (_cached.BootstrapBackupCreated
                && string.Equals(_cached.BootstrapBackupPath, backupPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _cached.BootstrapBackupCreated = true;
            _cached.BootstrapBackupPath = backupPath;
            SaveUnsafe(_cached);
        }

        Log.Info($"[BetterSaves] Recorded first-sync backup at '{backupPath}'.");
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
                var defaults = new BetterSavesConfigData
                {
                    BootstrapState = FirstSyncBootstrapState.Pending
                };
                SaveUnsafe(defaults);
                return defaults;
            }

            var json = File.ReadAllText(configPath);
            var hasBootstrapState = false;
            using (var document = JsonDocument.Parse(json))
            {
                hasBootstrapState = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.EnumerateObject()
                        .Any(property => property.NameEquals(nameof(BetterSavesConfigData.BootstrapState)));
            }

            var config = JsonSerializer.Deserialize<BetterSavesConfigData>(json, JsonOptions)
                ?? new BetterSavesConfigData();
            config.ModdedMultiplayerLocalPlayerIds ??= new Dictionary<string, ulong>();
            if (!hasBootstrapState)
            {
                config.BootstrapState = FirstSyncBootstrapState.Pending;
                SaveUnsafe(config);
                Log.Info("[BetterSaves] Migrated legacy config without first-sync bootstrap state to Pending.");
            }
            else if (config.BootstrapState == FirstSyncBootstrapState.Conflict)
            {
                config.BootstrapState = FirstSyncBootstrapState.Resolved;
            }
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
