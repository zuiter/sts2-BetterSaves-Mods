using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BetterSaves;

internal static class SaveInteropService
{
    internal enum ReconcilePreference
    {
        Auto,
        VanillaToModded,
        ModdedToVanilla
    }

    private static readonly HashSet<string> SaveOnlyPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/current_run.save",
            "saves/current_run.save.backup",
            "saves/current_run_mp.save",
            "saves/current_run_mp.save.backup"
        };

    private static readonly HashSet<string> DataOnlyExactPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/progress.save",
            "saves/progress.save.backup"
        };

    private static readonly HashSet<string> SinglePlayerDataGuardPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/progress.save",
            "saves/progress.save.backup",
            "saves/prefs.save",
            "saves/prefs.save.backup"
        };

    private static readonly HashSet<string> BootstrapPendingGuardExactPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/current_run.save",
            "saves/current_run.save.backup"
        };

    private static readonly string[] DataOnlyPrefixes =
    [
        "saves/history/",
        "saves/replays/"
    ];

    private static readonly HashSet<string> ProfileSelectionRecordingHooks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SavePrefsFile",
            "SaveProgressFile",
            "SaveRunHistory",
            "SaveRun",
            "EndSaveBatch",
            "DeleteCurrentRun",
            "DeleteCurrentMultiplayerRun"
        };

    private static readonly object InitLock = new();
    private static readonly object BootstrapPromptLock = new();
    private static readonly object BootstrapChoiceLock = new();
    private static readonly object DataOnlyCloudSyncGuardLock = new();
    private static readonly List<AccountSyncRoot> SyncRoots = [];
    private static readonly CancellationTokenSource ReconcileCancellation = new();
    private static readonly SemaphoreSlim ImmediateReconcileSemaphore = new(1, 1);
    private static readonly TimeSpan BootstrapChoiceLockDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WatcherDuplicateWindow = TimeSpan.FromSeconds(2);
    private static BootstrapPromptRequest? _pendingBootstrapPrompt;
    private static string? _bootstrapTargetAccountRoot;
    private static string? _bootstrapChoiceAccountRoot;
    private static int _bootstrapChoiceProfileIndex;
    private static ReconcilePreference _bootstrapChoicePreference;
    private static DateTime _bootstrapChoiceExpiresUtc;
    private static bool _isExecutingBootstrapImport;
    private static bool _skipBootstrapImportThisSession;
    private static bool _initialized;
    private static List<DataOnlyCloudSyncGuard> _pendingDataOnlyCloudSyncGuards = [];
    private static long _contentMutationVersion;

    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }

        try
        {
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2");
            var steamRoot = Path.Combine(appDataRoot, "steam");

            if (!Directory.Exists(steamRoot))
            {
                Log.Debug($"[BetterSaves] Steam save root was not found at '{steamRoot}'.");
                return;
            }

            var accountRoots = Directory.EnumerateDirectories(steamRoot).ToList();
            _bootstrapTargetAccountRoot = SelectBootstrapTargetAccountRoot(accountRoots);
            if (!string.IsNullOrEmpty(_bootstrapTargetAccountRoot))
            {
                Log.Info(
                    $"[BetterSaves] First-sync bootstrap will target account root '{_bootstrapTargetAccountRoot}'.");
            }

            if (!FirstSyncBackupService.EnsureBackup(accountRoots))
            {
                BetterSavesConfig.SetBootstrapState(
                    FirstSyncBootstrapState.Conflict,
                    "failed to create first-sync backup before startup reconciliation");
            }

            foreach (var accountRoot in accountRoots
                         .OrderByDescending(GetAccountRootLastActivityUtc)
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                SyncRoots.Add(new AccountSyncRoot(accountRoot));
            }

            RestorePreferredProfileSelectionForCurrentMode();
            _ = Task.Run(() => RunDelayedReconciliationPassesAsync(ReconcileCancellation.Token));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            Log.Info($"[BetterSaves] Initialized {SyncRoots.Count} save sync root(s).");
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to initialize save interop: {ex}");
        }
    }

    private static string? SelectBootstrapTargetAccountRoot(IEnumerable<string> accountRoots)
    {
        return accountRoots
            .OrderByDescending(GetAccountRootLastActivityUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool ShouldHandleBootstrapForAccountRoot(string accountRoot)
    {
        if (string.IsNullOrEmpty(_bootstrapTargetAccountRoot))
        {
            return true;
        }

        return string.Equals(
            _bootstrapTargetAccountRoot,
            accountRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime GetAccountRootLastActivityUtc(string accountRoot)
    {
        var candidatePaths = new List<string>
        {
            Path.Combine(accountRoot, "profile.save"),
            Path.Combine(accountRoot, "settings.save")
        };

        for (var profileIndex = 1; profileIndex <= 3; profileIndex++)
        {
            candidatePaths.Add(Path.Combine(accountRoot, $"profile{profileIndex}", "saves", "progress.save"));
            candidatePaths.Add(Path.Combine(accountRoot, $"profile{profileIndex}", "saves", "prefs.save"));
            candidatePaths.Add(Path.Combine(accountRoot, $"profile{profileIndex}", "saves", "current_run.save"));
            candidatePaths.Add(Path.Combine(accountRoot, "modded", $"profile{profileIndex}", "saves", "progress.save"));
            candidatePaths.Add(Path.Combine(accountRoot, "modded", $"profile{profileIndex}", "saves", "prefs.save"));
            candidatePaths.Add(Path.Combine(accountRoot, "modded", $"profile{profileIndex}", "saves", "current_run.save"));
        }

        return candidatePaths
            .Select(path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
    }

    private static void ActivateBootstrapChoiceLock(
        string accountRoot,
        int profileIndex,
        ReconcilePreference preference,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(accountRoot)
            || profileIndex is < 1 or > 3
            || preference == ReconcilePreference.Auto)
        {
            return;
        }

        lock (BootstrapChoiceLock)
        {
            _bootstrapChoiceAccountRoot = accountRoot;
            _bootstrapChoiceProfileIndex = profileIndex;
            _bootstrapChoicePreference = preference;
            _bootstrapChoiceExpiresUtc = DateTime.UtcNow.Add(BootstrapChoiceLockDuration);
        }

        Log.Info(
            $"[BetterSaves] Locked first-sync choice '{preference}' for profile{profileIndex} under '{accountRoot}' " +
            $"until {_bootstrapChoiceExpiresUtc:O} ({reason}).");
    }

    private static bool TryGetBootstrapChoiceLock(
        string accountRoot,
        int profileIndex,
        out ReconcilePreference preference)
    {
        lock (BootstrapChoiceLock)
        {
            if (_bootstrapChoiceExpiresUtc <= DateTime.UtcNow
                || string.IsNullOrWhiteSpace(_bootstrapChoiceAccountRoot))
            {
                _bootstrapChoiceAccountRoot = null;
                _bootstrapChoiceProfileIndex = 0;
                _bootstrapChoicePreference = ReconcilePreference.Auto;
                _bootstrapChoiceExpiresUtc = default;
                preference = ReconcilePreference.Auto;
                return false;
            }

            if (!string.Equals(_bootstrapChoiceAccountRoot, accountRoot, StringComparison.OrdinalIgnoreCase)
                || _bootstrapChoiceProfileIndex != profileIndex
                || _bootstrapChoicePreference == ReconcilePreference.Auto)
            {
                preference = ReconcilePreference.Auto;
                return false;
            }

            preference = _bootstrapChoicePreference;
            return true;
        }
    }

    private static void Shutdown()
    {
        lock (InitLock)
        {
            foreach (var syncRoot in SyncRoots)
            {
                syncRoot.ReconcileAllProfiles(
                    "shutdown",
                    VanillaModeCompatibilityPatches.StartupReconcilePreference);
                syncRoot.Dispose();
            }

            SyncRoots.Clear();
            ReconcileCancellation.Cancel();
        }
    }

    private static async Task RunDelayedReconciliationPassesAsync(CancellationToken cancellationToken)
    {
        var delays = new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(15)
        };

        foreach (var delay in delays)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (InitLock)
            {
                foreach (var syncRoot in SyncRoots)
                {
                    syncRoot.ReconcileAllProfiles(
                        $"delayed pass after {delay.TotalSeconds:0}s",
                        VanillaModeCompatibilityPatches.StartupReconcilePreference);
                }
            }
        }
    }

    public static void ReconcileNow(
        string reason,
        ReconcilePreference preference = ReconcilePreference.Auto)
    {
        if (!_initialized)
        {
            return;
        }

        ImmediateReconcileSemaphore.Wait();
        try
        {
            ReconcileAllProfilesUnsafe(reason, preference);
        }
        finally
        {
            ImmediateReconcileSemaphore.Release();
        }
    }

    public static void PropagateCurrentRunDeletion(
        string reason,
        bool isMultiplayer,
        ReconcilePreference preference)
    {
        if (!_initialized)
        {
            return;
        }

        ImmediateReconcileSemaphore.Wait();
        try
        {
            lock (InitLock)
            {
                foreach (var syncRoot in GetLikelyActiveSyncRoots())
                {
                    syncRoot.PropagateCurrentRunDeletion(reason, isMultiplayer, preference);
                }
            }
        }
        finally
        {
            ImmediateReconcileSemaphore.Release();
        }
    }

    public static void PurgeCompletedCurrentRun(
        string reason,
        bool isMultiplayer = false)
    {
        if (!_initialized)
        {
            return;
        }

        ImmediateReconcileSemaphore.Wait();
        try
        {
            lock (InitLock)
            {
                foreach (var syncRoot in GetLikelyActiveSyncRoots())
                {
                    syncRoot.PurgeCompletedCurrentRun(reason, isMultiplayer);
                }
            }
        }
        finally
        {
            ImmediateReconcileSemaphore.Release();
        }
    }

    public static void RestorePreferredProfileSelectionForCurrentMode()
    {
        if (!_initialized)
        {
            return;
        }

        var vanillaMode = VanillaModeCompatibilityPatches.IsCompatibilityModeEnabled;

        lock (InitLock)
        {
            foreach (var syncRoot in GetLikelyActiveSyncRoots())
            {
                syncRoot.RestorePreferredProfileSelection(vanillaMode);
            }
        }
    }

    public static long GetContentMutationVersion()
    {
        return Interlocked.Read(ref _contentMutationVersion);
    }

    public static void ReloadCurrentProfileFromDiskAfterInterop(string reason)
    {
        TryReloadCurrentProfileFromDisk(reason);
    }

    private static void RecordContentMutation()
    {
        Interlocked.Increment(ref _contentMutationVersion);
    }

    public static void BeginDataOnlyCloudSyncGuard()
    {
        if (!_initialized || BetterSavesConfig.CurrentMode != SyncMode.DataOnly)
        {
            return;
        }

        lock (DataOnlyCloudSyncGuardLock)
        {
            if (_pendingDataOnlyCloudSyncGuards.Count != 0)
            {
                return;
            }
        }

        var vanillaMode = VanillaModeCompatibilityPatches.IsCompatibilityModeEnabled;
        List<DataOnlyCloudSyncGuard> guards;
        lock (InitLock)
        {
            guards = GetLikelyActiveSyncRoots()
                .Select(root => root.TryCreateDataOnlyCloudSyncGuard(vanillaMode))
                .OfType<DataOnlyCloudSyncGuard>()
                .ToList();
        }

        if (guards.Count == 0)
        {
            return;
        }

        lock (DataOnlyCloudSyncGuardLock)
        {
            _pendingDataOnlyCloudSyncGuards = guards;
        }

        Log.Info(
            $"[BetterSaves] Captured {guards.Count} DataOnly current-run cloud-sync guard snapshot(s) before SyncCloudToLocal.");
    }

    public static void RestoreDataOnlyCloudSyncGuardAfterSync(string reason)
    {
        if (!_initialized)
        {
            return;
        }

        List<DataOnlyCloudSyncGuard> guards;
        lock (DataOnlyCloudSyncGuardLock)
        {
            if (_pendingDataOnlyCloudSyncGuards.Count == 0)
            {
                return;
            }

            guards = _pendingDataOnlyCloudSyncGuards;
            _pendingDataOnlyCloudSyncGuards = [];
        }

        ImmediateReconcileSemaphore.Wait();
        try
        {
            lock (InitLock)
            {
                foreach (var guard in guards)
                {
                    guard.Root.RestoreDataOnlyCloudSyncGuard(guard, reason);
                }
            }
        }
        finally
        {
            ImmediateReconcileSemaphore.Release();
        }
    }

    public static bool ShouldDeferDataOnlyMetadataReconcile(string methodName)
    {
        if (!_initialized || BetterSavesConfig.CurrentMode != SyncMode.DataOnly)
        {
            return false;
        }

        if (methodName is not ("SavePrefsFile" or "SaveProgressFile" or "EndSaveBatch"))
        {
            return false;
        }

        lock (InitLock)
        {
            foreach (var syncRoot in GetLikelyActiveSyncRoots())
            {
                if (!syncRoot.HasAnyActiveSinglePlayerCurrentRun())
                {
                    continue;
                }

                Log.Info(
                    $"[BetterSaves] Deferring DataOnly reconcile for '{methodName}' because an active single-player run is still in progress.");
                return true;
            }
        }

        return false;
    }

    public static bool TryGetPendingBootstrapPrompt(out BootstrapPromptRequest prompt)
    {
        lock (BootstrapPromptLock)
        {
            if (_pendingBootstrapPrompt is { } pending
                && BetterSavesConfig.IsBootstrapPending)
            {
                prompt = pending;
                return true;
            }
        }

        prompt = default;
        return false;
    }

    public static bool ConfirmPendingBootstrapPrompt(string source)
    {
        return ConfirmPendingBootstrapPrompt(source, null);
    }

    public static bool ConfirmPendingBootstrapPrompt(string source, BootstrapImportAction? overrideAction)
    {
        BootstrapPromptRequest request;
        lock (BootstrapPromptLock)
        {
            if (_pendingBootstrapPrompt is not { } pending)
            {
                return false;
            }

            request = pending;
        }

        ImmediateReconcileSemaphore.Wait();
        try
        {
            lock (InitLock)
            {
                var syncRoot = SyncRoots.FirstOrDefault(root =>
                    string.Equals(root.AccountRoot, request.AccountRoot, StringComparison.OrdinalIgnoreCase));
                if (syncRoot is null)
                {
                    return false;
                }

                var action = overrideAction ?? request.Action;
                if (action == BootstrapImportAction.None)
                {
                    return false;
                }

                _isExecutingBootstrapImport = true;
                try
                {
                    syncRoot.ExecuteBootstrapImport(request with { Action = action }, source);
                }
                finally
                {
                    _isExecutingBootstrapImport = false;
                }

                ActivateBootstrapChoiceLock(
                    request.AccountRoot,
                    request.ProfileIndex,
                    action == BootstrapImportAction.VanillaToModded
                        ? ReconcilePreference.VanillaToModded
                        : ReconcilePreference.ModdedToVanilla,
                    source);
                TryReloadCurrentProfileFromDisk(source);
                BetterSavesConfig.SetBootstrapState(
                    FirstSyncBootstrapState.Resolved,
                    $"user confirmed bootstrap import '{action}' ({source})");
                _skipBootstrapImportThisSession = false;
                lock (BootstrapPromptLock)
                {
                    _pendingBootstrapPrompt = null;
                }

                return true;
            }
        }
        finally
        {
            ImmediateReconcileSemaphore.Release();
        }
    }

    public static bool DeclinePendingBootstrapPrompt(string source)
    {
        lock (BootstrapPromptLock)
        {
            if (_pendingBootstrapPrompt is null)
            {
                return false;
            }

            _pendingBootstrapPrompt = null;
        }

        _skipBootstrapImportThisSession = true;
        BetterSavesConfig.SetBootstrapState(
            FirstSyncBootstrapState.Resolved,
            $"user skipped bootstrap import ({source})");
        return true;
    }

    public static void DismissPendingBootstrapPrompt(string source)
    {
        if (DeclinePendingBootstrapPrompt(source))
        {
            Log.Info($"[BetterSaves] Dismissed first-sync bootstrap prompt without import ({source}).");
        }
    }

    private static void TryReloadCurrentProfileFromDisk(string reason)
    {
        try
        {
            var saveManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.SaveManager");
            if (saveManagerType is null)
            {
                Log.Info("[BetterSaves] Could not resolve SaveManager type for post-import reload.");
                return;
            }

            var instanceProperty = AccessTools.Property(saveManagerType, "Instance");
            var saveManager = instanceProperty?.GetValue(null);
            if (saveManager is null)
            {
                Log.Info("[BetterSaves] Could not resolve SaveManager.Instance for post-import reload.");
                return;
            }

            var currentProfileProperty = AccessTools.Property(saveManagerType, "CurrentProfileId");
            if (currentProfileProperty?.GetValue(saveManager) is not int currentProfileId || currentProfileId <= 0)
            {
                Log.Info("[BetterSaves] Could not resolve current profile id for post-import reload.");
                return;
            }

            var initProfileMethod = AccessTools.Method(saveManagerType, "InitProfileId", [typeof(int?)]);
            if (initProfileMethod is not null)
            {
                initProfileMethod.Invoke(saveManager, [new int?(currentProfileId)]);
                Log.Info(
                    $"[BetterSaves] Reloaded current profile {currentProfileId} from disk after bootstrap import ({reason}) using InitProfileId.");
            }

            var switchProfileMethod = AccessTools.Method(saveManagerType, "SwitchProfileId", [typeof(int)]);
            if (switchProfileMethod is not null)
            {
                switchProfileMethod.Invoke(saveManager, [currentProfileId]);
                Log.Info(
                    $"[BetterSaves] Reloaded current profile {currentProfileId} from disk after bootstrap import ({reason}) using SwitchProfileId.");
            }

            var initPrefsMethod = AccessTools.Method(saveManagerType, "InitPrefsData", Type.EmptyTypes);
            initPrefsMethod?.Invoke(saveManager, []);

            var initProgressMethod = AccessTools.Method(saveManagerType, "InitProgressData", Type.EmptyTypes);
            initProgressMethod?.Invoke(saveManager, []);

            var initSettingsMethod = AccessTools.Method(saveManagerType, "InitSettingsData", Type.EmptyTypes);
            initSettingsMethod?.Invoke(saveManager, []);

            Log.Info(
                $"[BetterSaves] Refreshed current profile {currentProfileId} from disk after bootstrap import ({reason}) " +
                $"by reinitializing profile, prefs, progress, and settings data.");
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to reload current profile from disk after bootstrap import ({reason}): {ex}");
        }
    }

    private static void ReconcileAllProfilesUnsafe(string reason, ReconcilePreference preference)
    {
        lock (InitLock)
        {
            foreach (var syncRoot in SyncRoots)
            {
                syncRoot.ReconcileAllProfiles(reason, preference);
            }
        }
    }

    private static IEnumerable<AccountSyncRoot> GetLikelyActiveSyncRoots()
    {
        if (SyncRoots.Count <= 1)
        {
            return SyncRoots;
        }

        var rankedRoots = SyncRoots
            .Select(root => new
            {
                Root = root,
                LastActivityUtc = root.GetLastActivityUtc()
            })
            .OrderByDescending(entry => entry.LastActivityUtc)
            .ToList();

        if (rankedRoots.Count == 0)
        {
            return SyncRoots;
        }

        var newestActivity = rankedRoots[0].LastActivityUtc;
        if (newestActivity == DateTime.MinValue)
        {
            return SyncRoots;
        }

        return rankedRoots
            .Where(entry => entry.LastActivityUtc == newestActivity)
            .Select(entry => entry.Root)
            .ToList();
    }

    private sealed class AccountSyncRoot : IDisposable
    {
        private readonly string _accountRoot;
        private readonly FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, DateTime> _suppressedPaths =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, RecentProcessedChange> _recentProcessedChanges =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _recentProcessedDeletions =
            new(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        public AccountSyncRoot(string accountRoot)
        {
            _accountRoot = Path.GetFullPath(accountRoot);

            ReconcileAllProfiles("startup scan", VanillaModeCompatibilityPatches.StartupReconcilePreference);

            _watcher = new FileSystemWatcher(_accountRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;

            Log.Info($"[BetterSaves] Watching '{_accountRoot}'.");
        }

        public string AccountRoot => _accountRoot;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher.Dispose();
        }

        public void ReconcileAllProfiles(string reason, ReconcilePreference preference = ReconcilePreference.Auto)
        {
            for (var profileIndex = 1; profileIndex <= 3; profileIndex++)
            {
                SyncProfilePair(profileIndex, reason, preference);
            }
        }

        public DateTime GetLastActivityUtc()
        {
            return new[]
                {
                    Path.Combine(_accountRoot, "profile.save"),
                    Path.Combine(_accountRoot, "settings.save")
                }
                .Select(path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue)
                .Max();
        }

        public DataOnlyCloudSyncGuard? TryCreateDataOnlyCloudSyncGuard(bool vanillaMode)
        {
            var profileIndex = TryGetActiveProfileIndex();
            if (profileIndex is null)
            {
                return null;
            }

            var profileDir = GetProfileDirectory(profileIndex.Value, vanillaMode);
            var snapshots = new List<GuardedFileSnapshot>();

            foreach (var path in GetCurrentRunPairPaths(profileDir, isMultiplayer: false))
            {
                if (!TryReadStableFile(path, out var content, out var lastWriteTimeUtc))
                {
                    continue;
                }

                snapshots.Add(new GuardedFileSnapshot(path, content, lastWriteTimeUtc));
            }

            return snapshots.Count == 0
                ? null
                : new DataOnlyCloudSyncGuard(this, profileIndex.Value, vanillaMode, snapshots);
        }

        public void RestoreDataOnlyCloudSyncGuard(DataOnlyCloudSyncGuard guard, string reason)
        {
            if (!TryGetGuardedCurrentRunStartTime(guard, out var startTime))
            {
                Log.Info(
                    $"[BetterSaves] Skipping DataOnly current-run restore for profile{guard.ProfileIndex} under '{_accountRoot}' " +
                    $"because the guarded run files do not expose a valid start_time ({reason}).");
                return;
            }

            var vanillaProfileDir = Path.Combine(_accountRoot, $"profile{guard.ProfileIndex}");
            var moddedProfileDir = Path.Combine(_accountRoot, "modded", $"profile{guard.ProfileIndex}");
            var historyPaths = GetHistoryEntryPathsForStartTime(vanillaProfileDir, moddedProfileDir, startTime, isMultiplayer: false)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (historyPaths.Count > 0)
            {
                Log.Info(
                    $"[BetterSaves] Skipping DataOnly current-run restore for profile{guard.ProfileIndex} under '{_accountRoot}' " +
                    $"because matching history for start_time {startTime} already exists at '{string.Join("' and '", historyPaths)}' ({reason}).");
                return;
            }

            var restoredAny = false;

            foreach (var snapshot in guard.Files)
            {
                if (File.Exists(snapshot.Path))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(snapshot.Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SuppressPath(snapshot.Path);
                WriteFileWithRetries(snapshot.Path, snapshot.Content, snapshot.LastWriteTimeUtc);
                RecordContentMutation();
                restoredAny = true;
                Log.Info(
                    $"[BetterSaves] Restored local DataOnly current-run file '{snapshot.Path}' after cloud sync removed it ({reason}).");
            }

            if (restoredAny)
            {
                TryPurgeCompletedCurrentRunPair(vanillaProfileDir, moddedProfileDir, $"cloud-sync restore guard: {reason}", isMultiplayer: false);
            }
        }

        private static bool TryGetGuardedCurrentRunStartTime(DataOnlyCloudSyncGuard guard, out long startTime)
        {
            startTime = default;

            foreach (var snapshot in guard.Files)
            {
                if (TryGetCurrentRunStartTime(snapshot.Content, out startTime))
                {
                    return true;
                }
            }

            return false;
        }

        public void RestorePreferredProfileSelection(bool vanillaMode)
        {
            var currentProfileId = TryGetActiveProfileIndex();
            if (currentProfileId is null)
            {
                return;
            }

            if (BetterSavesConfig.CurrentMode == SyncMode.SaveOnly)
            {
                BetterSavesConfig.SetPreferredProfileId(true, currentProfileId.Value);
                BetterSavesConfig.SetPreferredProfileId(false, currentProfileId.Value);
                Log.Info(
                    $"[BetterSaves] Preserving current {(vanillaMode ? "vanilla" : "modded")} profile {currentProfileId.Value} " +
                    "for save-only mode based on profile.save.");
                return;
            }

            if (BetterSavesConfig.UsesSharedProfileSelection)
            {
                BetterSavesConfig.SetPreferredProfileId(true, currentProfileId.Value);
                BetterSavesConfig.SetPreferredProfileId(false, currentProfileId.Value);
                Log.Info(
                    $"[BetterSaves] Keeping current {(vanillaMode ? "vanilla" : "modded")} profile {currentProfileId.Value} " +
                    "and syncing preferred profile state to match profile.save.");
            }
            else
            {
                var preferredProfileId = BetterSavesConfig.GetPreferredProfileId(vanillaMode);
                if (preferredProfileId is >= 1 and <= 3 && preferredProfileId != currentProfileId.Value)
                {
                    SetActiveProfileIndex(
                        preferredProfileId,
                        $"restoring {(vanillaMode ? "vanilla" : "modded")} preferred profile");
                    return;
                }

                BetterSavesConfig.SetPreferredProfileId(vanillaMode, currentProfileId.Value);
                Log.Info(
                    $"[BetterSaves] Keeping current {(vanillaMode ? "vanilla" : "modded")} profile {currentProfileId.Value} " +
                    "for mode-specific profile selection.");
            }
        }

        public void PropagateCurrentRunDeletion(
            string reason,
            bool isMultiplayer,
            ReconcilePreference preference)
        {
            var profileIndex = TryGetActiveProfileIndex();
            if (profileIndex is null)
            {
                Log.Info($"[BetterSaves] Could not determine active profile for run deletion propagation ({reason}) under '{_accountRoot}'.");
                return;
            }

            var vanillaProfileDir = Path.Combine(_accountRoot, $"profile{profileIndex.Value}");
            var moddedProfileDir = Path.Combine(_accountRoot, "modded", $"profile{profileIndex.Value}");
            var targetRoots = preference switch
            {
                ReconcilePreference.VanillaToModded => new[] { moddedProfileDir },
                ReconcilePreference.ModdedToVanilla => new[] { vanillaProfileDir },
                ReconcilePreference.Auto => new[] { vanillaProfileDir, moddedProfileDir },
                _ => Array.Empty<string>()
            };

            if (targetRoots.Length == 0)
            {
                Log.Info($"[BetterSaves] Skipped run deletion propagation for unsupported preference '{preference}' ({reason}).");
                return;
            }

            var fileNames = isMultiplayer
                ? new[] { "current_run_mp.save", "current_run_mp.save.backup" }
                : new[] { "current_run.save", "current_run.save.backup" };

            foreach (var targetRoot in targetRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var fileName in fileNames)
                {
                    var targetPath = Path.Combine(targetRoot, "saves", fileName);
                    DeleteFileSafely(targetPath, reason);
                }
            }
        }

        public void PurgeCompletedCurrentRun(string reason, bool isMultiplayer)
        {
            var profileIndex = TryGetActiveProfileIndex();
            if (profileIndex is null)
            {
                Log.Info($"[BetterSaves] Could not determine active profile for stale run cleanup ({reason}) under '{_accountRoot}'.");
                return;
            }

            var vanillaProfileDir = Path.Combine(_accountRoot, $"profile{profileIndex.Value}");
            var moddedProfileDir = Path.Combine(_accountRoot, "modded", $"profile{profileIndex.Value}");
            TryPurgeCompletedCurrentRunPair(vanillaProfileDir, moddedProfileDir, reason, isMultiplayer);
        }

        private void SyncProfilePair(int profileIndex, string reason, ReconcilePreference preference)
        {
            var vanillaProfileDir = Path.Combine(_accountRoot, $"profile{profileIndex}");
            var moddedProfileDir = Path.Combine(_accountRoot, "modded", $"profile{profileIndex}");
            var activeProfileIndex = TryGetActiveProfileIndex();

            if (TryGetBootstrapChoiceLock(_accountRoot, profileIndex, out var lockedPreference))
            {
                preference = lockedPreference;
                Log.Info(
                    $"[BetterSaves] Applying locked first-sync choice '{preference}' to profile{profileIndex} ({reason}).");
            }

            if (!Directory.Exists(vanillaProfileDir) && !Directory.Exists(moddedProfileDir))
            {
                return;
            }

            if (TryHandleFreshInstallBootstrapProfilePair(
                    profileIndex,
                    activeProfileIndex,
                    vanillaProfileDir,
                    moddedProfileDir,
                    reason))
            {
                return;
            }

            if (TryCleanupPlaceholderProfilePair(
                    profileIndex,
                    activeProfileIndex,
                    vanillaProfileDir,
                    moddedProfileDir,
                    reason))
            {
                return;
            }

            if (BetterSavesConfig.IsSaveSyncEnabled)
            {
                TryPurgeCompletedCurrentRunPair(
                    vanillaProfileDir,
                    moddedProfileDir,
                    reason,
                    isMultiplayer: false);
            }

            if (preference == ReconcilePreference.VanillaToModded)
            {
                CopyTreeOneWay(vanillaProfileDir, moddedProfileDir, reason);
                return;
            }

            if (preference == ReconcilePreference.ModdedToVanilla)
            {
                CopyTreeOneWay(moddedProfileDir, vanillaProfileDir, reason);
                return;
            }

            var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectRelativeFiles(vanillaProfileDir, allFiles);
            CollectRelativeFiles(moddedProfileDir, allFiles);

            foreach (var relativeFile in allFiles
                         .Where(IsSyncableProfileRelativePath)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var vanillaPath = Path.Combine(vanillaProfileDir, relativeFile);
                var moddedPath = Path.Combine(moddedProfileDir, relativeFile);
                ReconcileFilePair(vanillaPath, moddedPath, reason);
            }
        }

        private bool TryHandleFreshInstallBootstrapProfilePair(
            int profileIndex,
            int? activeProfileIndex,
            string vanillaProfileDir,
            string moddedProfileDir,
            string reason)
        {
            if (!IsBootstrapReason(reason))
            {
                return false;
            }

            if (_skipBootstrapImportThisSession)
            {
                return true;
            }

            if (BetterSavesConfig.IsBootstrapPending
                && !ShouldHandleBootstrapForAccountRoot(_accountRoot))
            {
                if (activeProfileIndex is null || profileIndex == activeProfileIndex.Value)
                {
                    Log.Info(
                        $"[BetterSaves] Skipping first-sync bootstrap handling for non-target account root '{_accountRoot}' " +
                        $"while '{_bootstrapTargetAccountRoot}' remains the active bootstrap root ({reason}).");
                }

                return true;
            }

            if (activeProfileIndex is not null && profileIndex != activeProfileIndex.Value)
            {
                if (BetterSavesConfig.BootstrapState != FirstSyncBootstrapState.Resolved)
                {
                    Log.Info(
                        $"[BetterSaves] Deferring first-sync bootstrap handling for non-active profile{profileIndex} " +
                        $"while profile{activeProfileIndex.Value} remains the active slot ({reason}).");
                    return true;
                }

                return false;
            }

            if (BetterSavesConfig.IsBootstrapPending
                && TryGetPendingBootstrapPrompt(out var pendingPrompt)
                && string.Equals(pendingPrompt.AccountRoot, _accountRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var vanillaState = GetSinglePlayerDataState(vanillaProfileDir);
            var moddedState = GetSinglePlayerDataState(moddedProfileDir);

            var decision = EvaluateBootstrapDecision(profileIndex, vanillaState, moddedState, reason);
            if (!decision.ShouldHandle)
            {
                return false;
            }

            if (decision.LogMessage is not null)
            {
                Log.Info(decision.LogMessage);
            }

            if (decision.PromptKind is { } promptKind && decision.Action is { } action)
            {
                QueueBootstrapPrompt(
                    profileIndex,
                    promptKind,
                    action,
                    vanillaState,
                    moddedState,
                    reason);
            }

            return true;
        }

        private BootstrapDecision EvaluateBootstrapDecision(
            int profileIndex,
            SinglePlayerDataState vanillaState,
            SinglePlayerDataState moddedState,
            string reason)
        {
            if (BetterSavesConfig.BootstrapState == FirstSyncBootstrapState.Resolved)
            {
                return BootstrapDecision.None();
            }

            if (BetterSavesConfig.IsBootstrapConflict)
            {
                return BootstrapDecision.Block();
            }

            return BootstrapDecision.Prompt(
                BootstrapPromptKind.ChooseAuthoritativeSide,
                BootstrapImportAction.None,
                $"[BetterSaves] First-sync bootstrap is waiting for explicit user choice on profile{profileIndex}. " +
                $"Vanilla={vanillaState}; Modded={moddedState} ({reason}).");
        }

        private void QueueBootstrapPrompt(
            int profileIndex,
            BootstrapPromptKind kind,
            BootstrapImportAction action,
            SinglePlayerDataState vanillaState,
            SinglePlayerDataState moddedState,
            string reason)
        {
            var queued = false;
            lock (BootstrapPromptLock)
            {
                if (_pendingBootstrapPrompt is null)
                {
                    _pendingBootstrapPrompt = new BootstrapPromptRequest(
                        _accountRoot,
                        profileIndex,
                        kind,
                        action,
                        reason);
                    queued = true;
                }
            }

            if (queued)
            {
                Log.Info(
                    $"[BetterSaves] Queued first-sync bootstrap confirmation for profile{profileIndex} " +
                    $"with prompt '{kind}' and action '{action}'. Vanilla={vanillaState}; Modded={moddedState} ({reason}).");
            }
            else
            {
                Log.Info(
                    $"[BetterSaves] Ignored an additional first-sync bootstrap prompt for profile{profileIndex} under '{_accountRoot}' " +
                    $"because another bootstrap prompt is already pending. Vanilla={vanillaState}; Modded={moddedState} ({reason}).");
            }
        }

        public void ExecuteBootstrapImport(BootstrapPromptRequest request, string source)
        {
            var vanillaProfileDir = Path.Combine(_accountRoot, $"profile{request.ProfileIndex}");
            var moddedProfileDir = Path.Combine(_accountRoot, "modded", $"profile{request.ProfileIndex}");

            switch (request.Action)
            {
                case BootstrapImportAction.VanillaToModded:
                    CopyTreeOneWay(vanillaProfileDir, moddedProfileDir, $"confirmed bootstrap import ({source})");
                    break;
                case BootstrapImportAction.ModdedToVanilla:
                    CopyTreeOneWay(moddedProfileDir, vanillaProfileDir, $"confirmed bootstrap import ({source})");
                    break;
            }
        }

        private static bool IsBootstrapReason(string reason)
        {
            return string.Equals(reason, "startup scan", StringComparison.OrdinalIgnoreCase)
                || reason.StartsWith("delayed pass after ", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryCleanupPlaceholderProfilePair(
            int profileIndex,
            int? activeProfileIndex,
            string vanillaProfileDir,
            string moddedProfileDir,
            string reason)
        {
            if (activeProfileIndex == profileIndex)
            {
                return false;
            }

            var cleaned = false;

            if (IsPlaceholderProfileDirectory(vanillaProfileDir))
            {
                CleanupPlaceholderProfileDirectory(
                    vanillaProfileDir,
                    $"placeholder vanilla profile{profileIndex}: {reason}");
                cleaned = true;
            }

            if (IsPlaceholderProfileDirectory(moddedProfileDir))
            {
                CleanupPlaceholderProfileDirectory(
                    moddedProfileDir,
                    $"placeholder modded profile{profileIndex}: {reason}");
                cleaned = true;
            }

            if (cleaned)
            {
                Log.Info(
                    $"[BetterSaves] Cleaned placeholder profile slot {profileIndex} under '{_accountRoot}' " +
                    $"before syncing ({reason}).");
            }

            return cleaned;
        }

        private bool IsPlaceholderProfileDirectory(string profileDir)
        {
            if (!Directory.Exists(profileDir))
            {
                return false;
            }

            var savesDir = Path.Combine(profileDir, "saves");
            if (!Directory.Exists(savesDir))
            {
                return false;
            }

            if (Directory.Exists(Path.Combine(savesDir, "history"))
                && Directory.EnumerateFileSystemEntries(Path.Combine(savesDir, "history")).Any())
            {
                return false;
            }

            if (GetCurrentRunPairPaths(profileDir, isMultiplayer: false).Any(File.Exists)
                || GetCurrentRunPairPaths(profileDir, isMultiplayer: true).Any(File.Exists))
            {
                return false;
            }

            var progressSavePath = Path.Combine(savesDir, "progress.save");
            var prefsSavePath = Path.Combine(savesDir, "prefs.save");

            if (!File.Exists(progressSavePath) || !File.Exists(prefsSavePath))
            {
                return false;
            }

            try
            {
                var progressLength = new FileInfo(progressSavePath).Length;
                var prefsLength = new FileInfo(prefsSavePath).Length;
                if (progressLength > 1024 || prefsLength > 512)
                {
                    return false;
                }

                var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "progress.save",
                    "progress.save.backup",
                    "prefs.save",
                    "prefs.save.backup"
                };

                return Directory.EnumerateFiles(savesDir, "*", SearchOption.TopDirectoryOnly)
                    .All(path => allowedNames.Contains(Path.GetFileName(path)));
            }
            catch
            {
                return false;
            }
        }

        private void CleanupPlaceholderProfileDirectory(string profileDir, string reason)
        {
            var savesDir = Path.Combine(profileDir, "saves");
            if (!Directory.Exists(savesDir))
            {
                return;
            }

            foreach (var fileName in new[]
                     {
                         "progress.save",
                         "progress.save.backup",
                         "prefs.save",
                         "prefs.save.backup"
                     })
            {
                DeleteFileSafely(Path.Combine(savesDir, fileName), reason);
            }

            TryDeleteDirectoryIfEmpty(savesDir, reason);
            TryDeleteDirectoryIfEmpty(profileDir, reason);
        }

        private void TryDeleteDirectoryIfEmpty(string directoryPath, string reason)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    return;
                }

                Directory.Delete(directoryPath);
                Log.Info($"[BetterSaves] Deleted empty directory '{directoryPath}' ({reason}).");
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to delete empty directory '{directoryPath}' ({reason}): {ex}");
            }
        }

        private int? TryGetActiveProfileIndex()
        {
            var profileSavePath = Path.Combine(_accountRoot, "profile.save");
            if (!File.Exists(profileSavePath))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(profileSavePath));
                if (!document.RootElement.TryGetProperty("last_profile_id", out var profileIdElement))
                {
                    return null;
                }

                var profileIndex = profileIdElement.GetInt32();
                return profileIndex is >= 1 and <= 3 ? profileIndex : null;
            }
            catch
            {
                return null;
            }
        }

        private void SetActiveProfileIndex(int profileIndex, string reason)
        {
            if (profileIndex is < 1 or > 3)
            {
                return;
            }

            var profileSavePath = Path.Combine(_accountRoot, "profile.save");
            if (!File.Exists(profileSavePath))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(profileSavePath));
                var root = document.RootElement;
                var currentProfileId = root.TryGetProperty("last_profile_id", out var currentProfileElement)
                    ? currentProfileElement.GetInt32()
                    : 0;

                if (currentProfileId == profileIndex)
                {
                    return;
                }

                var schemaVersion = root.TryGetProperty("schema_version", out var schemaVersionElement)
                    ? schemaVersionElement.GetInt32()
                    : 2;

                var payload = JsonSerializer.Serialize(new
                {
                    last_profile_id = profileIndex,
                    schema_version = schemaVersion
                });

                File.WriteAllText(profileSavePath, payload);
                RecordContentMutation();
                Log.Info(
                    $"[BetterSaves] Set active profile to {profileIndex} under '{_accountRoot}' " +
                    $"({reason}; previous profile was {currentProfileId}).");
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to set active profile under '{_accountRoot}' ({reason}): {ex}");
            }
        }

        private bool HasSinglePlayerCurrentRun(int profileIndex, bool vanillaMode)
        {
            var profileDir = GetProfileDirectory(profileIndex, vanillaMode);
            return GetCurrentRunPairPaths(profileDir, isMultiplayer: false).Any(File.Exists);
        }

        public bool HasAnyActiveSinglePlayerCurrentRun()
        {
            var activeProfileIndex = TryGetActiveProfileIndex();
            return activeProfileIndex is not null
                   && (HasSinglePlayerCurrentRun(activeProfileIndex.Value, vanillaMode: true)
                       || HasSinglePlayerCurrentRun(activeProfileIndex.Value, vanillaMode: false));
        }

        private string GetProfileDirectory(int profileIndex, bool vanillaMode)
        {
            return Path.Combine(
                _accountRoot,
                vanillaMode ? $"profile{profileIndex}" : Path.Combine("modded", $"profile{profileIndex}"));
        }

        private void CopyTreeOneWay(string sourceRoot, string targetRoot, string reason)
        {
            if (!Directory.Exists(sourceRoot))
            {
                return;
            }

            var relativeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectRelativeFiles(sourceRoot, relativeFiles);

            foreach (var relativeFile in relativeFiles
                         .Where(IsSyncableProfileRelativePath)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var sourcePath = Path.Combine(sourceRoot, relativeFile);
                var targetPath = Path.Combine(targetRoot, relativeFile);
                CopyFileSafely(sourcePath, targetPath, reason);
            }
        }

        private static void CollectRelativeFiles(string directoryPath, HashSet<string> destination)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                destination.Add(Path.GetRelativePath(directoryPath, filePath));
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs eventArgs)
        {
            if (_disposed || IsSuppressed(eventArgs.FullPath))
            {
                return;
            }

            if (ShouldSkipDuplicateChange(eventArgs.FullPath))
            {
                return;
            }

            var preference = GetEffectiveReconcilePreferenceForPath(
                eventArgs.FullPath,
                VanillaModeCompatibilityPatches.CurrentReconcilePreference);
            if (!ShouldProcessPathForPreference(eventArgs.FullPath, preference))
            {
                VanillaModeCompatibilityPatches.TryFinalizeSessionReconcilePreferenceOverrideForSourcePath(eventArgs.FullPath);
                return;
            }

            if (!IsSyncablePath(eventArgs.FullPath))
            {
                return;
            }

            if (!TryGetCounterpartPath(eventArgs.FullPath, out var counterpartPath))
            {
                return;
            }

            if (Directory.Exists(eventArgs.FullPath) || !File.Exists(eventArgs.FullPath))
            {
                return;
            }

            if (preference == ReconcilePreference.Auto)
            {
                ReconcileFilePair(eventArgs.FullPath, counterpartPath, eventArgs.ChangeType.ToString());
                return;
            }

            CopyFileSafely(eventArgs.FullPath, counterpartPath, eventArgs.ChangeType.ToString());
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs eventArgs)
        {
            if (_disposed || IsSuppressed(eventArgs.FullPath))
            {
                return;
            }

            if (ShouldSkipDuplicateDeletion(eventArgs.FullPath))
            {
                return;
            }

            var preference = GetEffectiveReconcilePreferenceForPath(
                eventArgs.FullPath,
                VanillaModeCompatibilityPatches.CurrentReconcilePreference);
            if (!ShouldProcessPathForPreference(eventArgs.FullPath, preference))
            {
                VanillaModeCompatibilityPatches.TryFinalizeSessionReconcilePreferenceOverrideForSourcePath(eventArgs.FullPath);
                return;
            }

            if (!IsSyncablePath(eventArgs.FullPath))
            {
                return;
            }

            if (ShouldIgnoreDeletion(eventArgs.FullPath, preference))
            {
                return;
            }

            if (!TryGetCounterpartPath(eventArgs.FullPath, out var counterpartPath))
            {
                return;
            }

            RememberProcessedDeletion(eventArgs.FullPath);
            DeleteFileSafely(counterpartPath, eventArgs.ChangeType.ToString());
        }

        private static bool ShouldIgnoreDeletion(string sourcePath, ReconcilePreference preference)
        {
            if (preference != ReconcilePreference.Auto)
            {
                return true;
            }

            if (!VanillaModeCompatibilityPatches.IsCompatibilityModeEnabled)
            {
                return false;
            }

            if (IsModdedPath(sourcePath))
            {
                return false;
            }

            var normalizedPath = sourcePath.Replace('/', '\\');
            return normalizedPath.Contains("\\history\\", StringComparison.OrdinalIgnoreCase);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs eventArgs)
        {
            if (_disposed)
            {
                return;
            }

            var preference = GetEffectiveReconcilePreferenceForPath(
                eventArgs.FullPath,
                VanillaModeCompatibilityPatches.CurrentReconcilePreference);
            if (!ShouldProcessPathForPreference(eventArgs.FullPath, preference))
            {
                VanillaModeCompatibilityPatches.TryFinalizeSessionReconcilePreferenceOverrideForSourcePath(eventArgs.FullPath);
                return;
            }

            if (!IsSyncablePath(eventArgs.FullPath))
            {
                return;
            }

            if (!IsSuppressed(eventArgs.OldFullPath)
                && !ShouldSkipDuplicateDeletion(eventArgs.OldFullPath)
                && TryGetCounterpartPath(eventArgs.OldFullPath, out var oldCounterpartPath))
            {
                RememberProcessedDeletion(eventArgs.OldFullPath);
                DeleteFileSafely(oldCounterpartPath, "Renamed");
            }

            OnFileChanged(sender, eventArgs);
        }

        private bool ShouldSkipDuplicateChange(string path)
        {
            if (!_recentProcessedChanges.TryGetValue(path, out var recent))
            {
                return false;
            }

            if (recent.ExpiresUtc <= DateTime.UtcNow)
            {
                _recentProcessedChanges.TryRemove(path, out _);
                return false;
            }

            var snapshot = FileSnapshot.TryCreate(path);
            return snapshot is not null
                   && snapshot.Value.Length == recent.Length
                   && snapshot.Value.LastWriteTimeUtc == recent.LastWriteTimeUtc;
        }

        private void RememberProcessedChange(string path, long length, DateTime lastWriteTimeUtc)
        {
            _recentProcessedChanges[path] = new RecentProcessedChange(
                length,
                lastWriteTimeUtc,
                DateTime.UtcNow.Add(WatcherDuplicateWindow));
        }

        private bool ShouldSkipDuplicateDeletion(string path)
        {
            if (!_recentProcessedDeletions.TryGetValue(path, out var expiration))
            {
                return false;
            }

            if (expiration <= DateTime.UtcNow)
            {
                _recentProcessedDeletions.TryRemove(path, out _);
                return false;
            }

            return true;
        }

        private void RememberProcessedDeletion(string path)
        {
            _recentProcessedDeletions[path] = DateTime.UtcNow.Add(WatcherDuplicateWindow);
        }

        private ReconcilePreference GetEffectiveReconcilePreferenceForPath(
            string sourcePath,
            ReconcilePreference fallbackPreference)
        {
            if (!TryGetProfileIndexAndModeFromPath(sourcePath, out var profileIndex, out _)
                || !TryGetBootstrapChoiceLock(_accountRoot, profileIndex, out var lockedPreference))
            {
                return fallbackPreference;
            }

            Log.Info(
                $"[BetterSaves] Applying locked first-sync choice '{lockedPreference}' to path '{sourcePath}'.");
            return lockedPreference;
        }

        private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
        {
            Log.Info($"[BetterSaves] File watcher error under '{_accountRoot}': {eventArgs.GetException()}");
        }

        private static bool ShouldProcessPathForPreference(string sourcePath, ReconcilePreference preference)
        {
            if (preference == ReconcilePreference.Auto)
            {
                return true;
            }

            var isModdedPath = IsModdedPath(sourcePath);
            return preference switch
            {
                ReconcilePreference.VanillaToModded => !isModdedPath,
                ReconcilePreference.ModdedToVanilla => isModdedPath,
                _ => true
            };
        }

        private static bool IsModdedPath(string path)
        {
            return path.Replace('/', '\\').Contains("\\modded\\", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSyncablePath(string sourcePath)
        {
            return TryGetProfileRelativePath(sourcePath, out var profileRelativePath)
                && IsSyncableProfileRelativePath(profileRelativePath);
        }

        private static bool IsSyncableProfileRelativePath(string profileRelativePath)
        {
            var normalized = NormalizeRelativePath(profileRelativePath);
            return BetterSavesConfig.CurrentMode switch
            {
                SyncMode.SaveOnly => SaveOnlyPaths.Contains(normalized),
                SyncMode.DataOnly => IsDataOnlyProfileRelativePath(normalized),
                SyncMode.FullSync => !IsEphemeralProfileRelativePath(normalized),
                _ => false
            };
        }

        private static bool IsDataOnlyProfileRelativePath(string normalizedProfileRelativePath)
        {
            if (IsEphemeralProfileRelativePath(normalizedProfileRelativePath))
            {
                return false;
            }

            return DataOnlyExactPaths.Contains(normalizedProfileRelativePath)
                || DataOnlyPrefixes.Any(prefix =>
                    normalizedProfileRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsBootstrapPendingProtectedProfileRelativePath(string normalizedProfileRelativePath)
        {
            if (IsEphemeralProfileRelativePath(normalizedProfileRelativePath))
            {
                return false;
            }

            return SinglePlayerDataGuardPaths.Contains(normalizedProfileRelativePath)
                || BootstrapPendingGuardExactPaths.Contains(normalizedProfileRelativePath)
                || normalizedProfileRelativePath.StartsWith("saves/history/", StringComparison.OrdinalIgnoreCase)
                || normalizedProfileRelativePath.StartsWith("replays/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEphemeralProfileRelativePath(string normalizedProfileRelativePath)
        {
            return normalizedProfileRelativePath.Contains(".tmp", StringComparison.OrdinalIgnoreCase)
                || normalizedProfileRelativePath.Contains(".val.corrupt", StringComparison.OrdinalIgnoreCase)
                || normalizedProfileRelativePath.Contains(".backup.backup", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetProfileRelativePath(string sourcePath, out string profileRelativePath)
        {
            profileRelativePath = string.Empty;

            string relativePath;
            try
            {
                var fullSourcePath = Path.GetFullPath(sourcePath);
                if (!fullSourcePath.StartsWith(_accountRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                relativePath = Path.GetRelativePath(_accountRoot, fullSourcePath);
            }
            catch
            {
                return false;
            }

            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 3)
            {
                return false;
            }

            var segmentIndex = 0;
            if (segments[0].Equals("modded", StringComparison.OrdinalIgnoreCase))
            {
                segmentIndex = 1;
            }

            if (segmentIndex >= segments.Length || !IsTrackedProfileDirectory(segments[segmentIndex]))
            {
                return false;
            }

            if (segmentIndex >= segments.Length - 1)
            {
                return false;
            }

            profileRelativePath = string.Join('/', segments[(segmentIndex + 1)..]);
            return true;
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        private bool TryGetCounterpartPath(string sourcePath, out string counterpartPath)
        {
            counterpartPath = string.Empty;

            string relativePath;
            try
            {
                var fullSourcePath = Path.GetFullPath(sourcePath);
                if (!fullSourcePath.StartsWith(_accountRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                relativePath = Path.GetRelativePath(_accountRoot, fullSourcePath);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 2)
            {
                return false;
            }

            var segmentIndex = 0;
            var isModdedPath = false;

            if (segments[0].Equals("modded", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length < 3)
                {
                    return false;
                }

                isModdedPath = true;
                segmentIndex = 1;
            }

            if (!IsTrackedProfileDirectory(segments[segmentIndex]))
            {
                return false;
            }

            if (segmentIndex == segments.Length - 1)
            {
                return false;
            }

            var counterpartSegments = new List<string>();
            if (!isModdedPath)
            {
                counterpartSegments.Add("modded");
            }

            counterpartSegments.Add(segments[segmentIndex]);

            for (var i = segmentIndex + 1; i < segments.Length; i++)
            {
                counterpartSegments.Add(segments[i]);
            }

            counterpartPath = Path.Combine(_accountRoot, Path.Combine(counterpartSegments.ToArray()));
            return true;
        }

        private static bool IsTrackedProfileDirectory(string directoryName)
        {
            return directoryName.Equals("profile1", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals("profile2", StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals("profile3", StringComparison.OrdinalIgnoreCase);
        }

        private void ReconcileFilePair(string pathA, string pathB, string reason)
        {
            var preferredSource = DeterminePreferredSource(pathA, pathB);

            switch (preferredSource)
            {
                case SourceSide.First:
                    CopyFileSafely(pathA, pathB, reason);
                    break;
                case SourceSide.Second:
                    CopyFileSafely(pathB, pathA, reason);
                    break;
            }
        }

        private SourceSide DeterminePreferredSource(string firstPath, string secondPath)
        {
            if (TryDetermineBootstrapProtectedPreferredSource(firstPath, secondPath, out var protectedSource))
            {
                return protectedSource;
            }

            if (TryDetermineProgressSemanticPreferredSource(firstPath, secondPath, out var progressSemanticSource))
            {
                return progressSemanticSource;
            }

            if (TryDetermineSinglePlayerDataPreferredSource(firstPath, secondPath, out var guardedSource))
            {
                return guardedSource;
            }

            var firstSnapshot = FileSnapshot.TryCreate(firstPath);
            var secondSnapshot = FileSnapshot.TryCreate(secondPath);

            if (firstSnapshot is null && secondSnapshot is null)
            {
                return SourceSide.None;
            }

            if (firstSnapshot is null)
            {
                return SourceSide.Second;
            }

            if (secondSnapshot is null)
            {
                return SourceSide.First;
            }

            if (firstSnapshot.Value.LastWriteTimeUtc == secondSnapshot.Value.LastWriteTimeUtc
                && firstSnapshot.Value.Length == secondSnapshot.Value.Length)
            {
                return SourceSide.None;
            }

            var timestampComparison = firstSnapshot.Value.LastWriteTimeUtc.CompareTo(
                secondSnapshot.Value.LastWriteTimeUtc);

            if (timestampComparison > 0)
            {
                return SourceSide.First;
            }

            if (timestampComparison < 0)
            {
                return SourceSide.Second;
            }

            return firstSnapshot.Value.Length >= secondSnapshot.Value.Length
                ? SourceSide.First
                : SourceSide.Second;
        }

        private bool TryDetermineProgressSemanticPreferredSource(
            string firstPath,
            string secondPath,
            out SourceSide preferredSource)
        {
            preferredSource = SourceSide.None;

            if (!TryGetProfileRelativePath(firstPath, out var firstRelativePath)
                || !TryGetProfileRelativePath(secondPath, out var secondRelativePath))
            {
                return false;
            }

            var normalizedFirstRelativePath = NormalizeRelativePath(firstRelativePath);
            if (!normalizedFirstRelativePath.Equals(
                    NormalizeRelativePath(secondRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                || normalizedFirstRelativePath is not ("saves/progress.save" or "saves/progress.save.backup"))
            {
                return false;
            }

            var firstState = GetProgressSemanticState(firstPath);
            var secondState = GetProgressSemanticState(secondPath);

            if (firstState.IsMeaningfullyRicherThan(secondState))
            {
                preferredSource = SourceSide.First;
                Log.Info(
                    $"[BetterSaves] Preferred semantically richer progress source '{firstPath}' over '{secondPath}' " +
                    $"for '{normalizedFirstRelativePath}'. First={firstState}; Second={secondState}.");
                return true;
            }

            if (secondState.IsMeaningfullyRicherThan(firstState))
            {
                preferredSource = SourceSide.Second;
                Log.Info(
                    $"[BetterSaves] Preferred semantically richer progress source '{secondPath}' over '{firstPath}' " +
                    $"for '{normalizedFirstRelativePath}'. First={firstState}; Second={secondState}.");
                return true;
            }

            return false;
        }

        private bool TryDetermineBootstrapProtectedPreferredSource(
            string firstPath,
            string secondPath,
            out SourceSide preferredSource)
        {
            preferredSource = SourceSide.None;

            if (_isExecutingBootstrapImport
                || (!BetterSavesConfig.IsBootstrapPending && !BetterSavesConfig.IsBootstrapConflict))
            {
                return false;
            }

            if (!TryGetProfileRelativePath(firstPath, out var firstRelativePath)
                || !TryGetProfileRelativePath(secondPath, out var secondRelativePath))
            {
                return false;
            }

            var normalizedFirstRelativePath = NormalizeRelativePath(firstRelativePath);
            if (!normalizedFirstRelativePath.Equals(
                    NormalizeRelativePath(secondRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                || !IsBootstrapPendingProtectedProfileRelativePath(normalizedFirstRelativePath))
            {
                return false;
            }

            Log.Info(
                $"[BetterSaves] First-sync bootstrap is not resolved for '{normalizedFirstRelativePath}'. " +
                $"Skipping automatic overwrite between '{firstPath}' and '{secondPath}'.");
            return true;
        }

        private bool TryDetermineSinglePlayerDataPreferredSource(
            string firstPath,
            string secondPath,
            out SourceSide preferredSource)
        {
            preferredSource = SourceSide.None;

            if (!TryGetProfileRelativePath(firstPath, out var firstRelativePath)
                || !TryGetProfileRelativePath(secondPath, out var secondRelativePath))
            {
                return false;
            }

            var normalizedFirstRelativePath = NormalizeRelativePath(firstRelativePath);
            if (!normalizedFirstRelativePath.Equals(
                    NormalizeRelativePath(secondRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                || !SinglePlayerDataGuardPaths.Contains(normalizedFirstRelativePath))
            {
                return false;
            }

            var firstProfileDir = TryGetProfileDirectory(firstPath);
            var secondProfileDir = TryGetProfileDirectory(secondPath);
            if (string.IsNullOrEmpty(firstProfileDir) || string.IsNullOrEmpty(secondProfileDir))
            {
                return false;
            }

            var firstState = GetSinglePlayerDataState(firstProfileDir);
            var secondState = GetSinglePlayerDataState(secondProfileDir);

            if (firstState.IsLowDataSinglePlayerProfile && secondState.IsClearlyRicherThan(firstState))
            {
                preferredSource = SourceSide.Second;
                if (BetterSavesConfig.IsBootstrapConflict)
                {
                    BetterSavesConfig.SetBootstrapState(
                        FirstSyncBootstrapState.Resolved,
                        $"bootstrap conflict resolved by preferring richer single-player source for '{normalizedFirstRelativePath}'");
                }
                Log.Info(
                    $"[BetterSaves] Preferred richer single-player source '{secondPath}' over low-data '{firstPath}' " +
                    $"for '{normalizedFirstRelativePath}'. First={firstState}; Second={secondState}.");
                return true;
            }

            if (secondState.IsLowDataSinglePlayerProfile && firstState.IsClearlyRicherThan(secondState))
            {
                preferredSource = SourceSide.First;
                if (BetterSavesConfig.IsBootstrapConflict)
                {
                    BetterSavesConfig.SetBootstrapState(
                        FirstSyncBootstrapState.Resolved,
                        $"bootstrap conflict resolved by preferring richer single-player source for '{normalizedFirstRelativePath}'");
                }
                Log.Info(
                    $"[BetterSaves] Preferred richer single-player source '{firstPath}' over low-data '{secondPath}' " +
                    $"for '{normalizedFirstRelativePath}'. First={firstState}; Second={secondState}.");
                return true;
            }

            if (!_isExecutingBootstrapImport && BetterSavesConfig.IsBootstrapPending)
            {
                preferredSource = SourceSide.None;
                Log.Info(
                    $"[BetterSaves] First-sync bootstrap is still pending confirmation for '{normalizedFirstRelativePath}'. " +
                    $"Skipping automatic overwrite between '{firstPath}' and '{secondPath}'. " +
                    $"First={firstState}; Second={secondState}.");
                return true;
            }

            if (!_isExecutingBootstrapImport
                && BetterSavesConfig.IsBootstrapConflict
                && firstState.HasAnySinglePlayerData
                && secondState.HasAnySinglePlayerData)
            {
                preferredSource = SourceSide.None;
                Log.Info(
                    $"[BetterSaves] Bootstrap conflict is still unresolved for '{normalizedFirstRelativePath}'. " +
                    $"Skipping automatic overwrite between '{firstPath}' and '{secondPath}'. " +
                    $"First={firstState}; Second={secondState}.");
                return true;
            }

            return false;
        }

        private void CopyFileSafely(
            string sourcePath,
            string targetPath,
            string reason,
            bool allowSinglePlayerDataProtection = true)
        {
            try
            {
                if (TryProtectPendingBootstrapFile(sourcePath, targetPath, reason))
                {
                    return;
                }

                if (TryPurgeStaleCurrentRunPair(sourcePath, targetPath, reason))
                {
                    return;
                }

                if (allowSinglePlayerDataProtection
                    && TryProtectLowDataSinglePlayerData(sourcePath, targetPath, reason))
                {
                    return;
                }

                if (!TryReadStableFile(sourcePath, out var content, out var lastWriteTimeUtc))
                {
                    return;
                }

                if (TryNormalizeMultiplayerRunForTarget(sourcePath, targetPath, content, out var normalizedContent, out var normalizationLog))
                {
                    content = normalizedContent;
                    Log.Info($"[BetterSaves] {normalizationLog}");
                }

                if (TargetAlreadyMatches(targetPath, content.LongLength, lastWriteTimeUtc))
                {
                    return;
                }

                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                SuppressPath(targetPath);
                WriteFileWithRetries(targetPath, content, lastWriteTimeUtc);
                RecordContentMutation();
                RememberProcessedChange(sourcePath, content.LongLength, lastWriteTimeUtc);
                CloudMirrorService.MirrorFile(_accountRoot, targetPath);
                MaybeRecordPreferredProfileFromPath(sourcePath, reason);

                Log.Info($"[BetterSaves] Mirrored '{sourcePath}' to '{targetPath}' ({reason}).");
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to mirror '{sourcePath}' to '{targetPath}' ({reason}): {ex}");
            }
        }

        private bool TryProtectPendingBootstrapFile(string sourcePath, string targetPath, string reason)
        {
            if (_isExecutingBootstrapImport
                || (!BetterSavesConfig.IsBootstrapPending && !BetterSavesConfig.IsBootstrapConflict))
            {
                return false;
            }

            if (!TryGetProfileRelativePath(sourcePath, out var sourceRelativePath)
                || !TryGetProfileRelativePath(targetPath, out var targetRelativePath))
            {
                return false;
            }

            var normalizedSourceRelativePath = NormalizeRelativePath(sourceRelativePath);
            if (!normalizedSourceRelativePath.Equals(
                    NormalizeRelativePath(targetRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                || !IsBootstrapPendingProtectedProfileRelativePath(normalizedSourceRelativePath))
            {
                return false;
            }

            Log.Info(
                $"[BetterSaves] First-sync bootstrap is not resolved for '{normalizedSourceRelativePath}'. " +
                $"Skipping automatic overwrite between '{sourcePath}' and '{targetPath}' ({reason}).");
            return true;
        }

        private bool TryProtectLowDataSinglePlayerData(string sourcePath, string targetPath, string reason)
        {
            if (!TryGetProfileRelativePath(sourcePath, out var sourceRelativePath)
                || !TryGetProfileRelativePath(targetPath, out var targetRelativePath))
            {
                return false;
            }

            var normalizedSourceRelativePath = NormalizeRelativePath(sourceRelativePath);
            if (!normalizedSourceRelativePath.Equals(
                    NormalizeRelativePath(targetRelativePath),
                    StringComparison.OrdinalIgnoreCase)
                || !SinglePlayerDataGuardPaths.Contains(normalizedSourceRelativePath))
            {
                return false;
            }

            var sourceProfileDir = TryGetProfileDirectory(sourcePath);
            var targetProfileDir = TryGetProfileDirectory(targetPath);
            if (string.IsNullOrEmpty(sourceProfileDir) || string.IsNullOrEmpty(targetProfileDir))
            {
                return false;
            }

            var sourceState = GetSinglePlayerDataState(sourceProfileDir);
            var targetState = GetSinglePlayerDataState(targetProfileDir);

            if (!_isExecutingBootstrapImport && BetterSavesConfig.IsBootstrapPending)
            {
                Log.Info(
                    $"[BetterSaves] First-sync bootstrap is still pending confirmation for '{normalizedSourceRelativePath}'. " +
                    $"Skipping automatic overwrite between '{sourcePath}' and '{targetPath}'. " +
                    $"Source={sourceState}; Target={targetState} ({reason}).");
                return true;
            }

            if (!_isExecutingBootstrapImport && BetterSavesConfig.IsBootstrapConflict)
            {
                Log.Info(
                    $"[BetterSaves] First-sync bootstrap is in conflict state for '{normalizedSourceRelativePath}'. " +
                    $"Skipping automatic overwrite between '{sourcePath}' and '{targetPath}'. " +
                    $"Source={sourceState}; Target={targetState} ({reason}).");
                return true;
            }

            if (!sourceState.IsLowDataSinglePlayerProfile
                || !targetState.IsClearlyRicherThan(sourceState)
                || !File.Exists(targetPath))
            {
                return false;
            }

            Log.Info(
                $"[BetterSaves] Prevented low-data single-player file '{normalizedSourceRelativePath}' " +
                $"from overwriting a richer counterpart. " +
                $"Source '{sourceProfileDir}' => {sourceState}; " +
                $"target '{targetProfileDir}' => {targetState} ({reason}).");

            CopyFileSafely(
                targetPath,
                sourcePath,
                $"protected mature single-player data: {reason}",
                allowSinglePlayerDataProtection: false);
            return true;
        }

        private void MaybeRecordPreferredProfileFromPath(string sourcePath, string reason)
        {
            if (!TryGetSaveHookName(reason, out var hookName)
                || !ProfileSelectionRecordingHooks.Contains(hookName))
            {
                return;
            }

            if (!TryGetProfileIndexAndModeFromPath(sourcePath, out var profileIndex, out var vanillaMode))
            {
                return;
            }

            if (BetterSavesConfig.UsesSharedProfileSelection)
            {
                BetterSavesConfig.SetPreferredProfileId(true, profileIndex);
                BetterSavesConfig.SetPreferredProfileId(false, profileIndex);
                Log.Info(
                    $"[BetterSaves] Recorded shared preferred profile {profileIndex} " +
                    $"from {(vanillaMode ? "vanilla" : "modded")} path '{sourcePath}' ({reason}).");
            }
            else
            {
                BetterSavesConfig.SetPreferredProfileId(vanillaMode, profileIndex);
                Log.Info(
                    $"[BetterSaves] Recorded {(vanillaMode ? "vanilla" : "modded")} preferred profile {profileIndex} " +
                    $"from path '{sourcePath}' ({reason}).");
            }
        }

        private static bool TryGetSaveHookName(string reason, out string hookName)
        {
            hookName = string.Empty;

            if (string.IsNullOrWhiteSpace(reason)
                || !reason.StartsWith("save hook:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            hookName = reason["save hook:".Length..].Trim();
            return !string.IsNullOrWhiteSpace(hookName);
        }

        private bool TryPurgeStaleCurrentRunPair(string sourcePath, string targetPath, string reason)
        {
            if (!IsSinglePlayerCurrentRunPath(sourcePath) && !IsSinglePlayerCurrentRunPath(targetPath))
            {
                return false;
            }

            var sourceProfileDir = TryGetProfileDirectory(sourcePath);
            var targetProfileDir = TryGetProfileDirectory(targetPath);

            if (string.IsNullOrEmpty(sourceProfileDir) || string.IsNullOrEmpty(targetProfileDir))
            {
                return false;
            }

            return TryPurgeCompletedCurrentRunPair(
                sourceProfileDir,
                targetProfileDir,
                reason,
                isMultiplayer: false);
        }

        private bool TryPurgeCompletedCurrentRunPair(
            string firstProfileDir,
            string secondProfileDir,
            string reason,
            bool isMultiplayer)
        {
            if (!TryGetCurrentRunStartTime(firstProfileDir, secondProfileDir, isMultiplayer, out var startTime))
            {
                return false;
            }

            var historyPaths = GetHistoryEntryPathsForStartTime(firstProfileDir, secondProfileDir, startTime, isMultiplayer)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (historyPaths.Count == 0)
            {
                return false;
            }

            var stalePaths = GetCurrentRunPairPaths(firstProfileDir, isMultiplayer)
                .Concat(GetCurrentRunPairPaths(secondProfileDir, isMultiplayer))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (stalePaths.Count == 0)
            {
                return false;
            }

            Log.Info(
                $"[BetterSaves] Detected stale {(isMultiplayer ? "multiplayer " : string.Empty)}current run pair with " +
                $"start_time {startTime}. Matching history exists at '{string.Join("' and '", historyPaths)}'. " +
                $"Deleting stale run files instead of mirroring them ({reason}).");

            foreach (var stalePath in stalePaths)
            {
                DeleteFileSafely(stalePath, $"stale current run cleanup: {reason}");
            }

            return true;
        }

        private static bool IsSinglePlayerCurrentRunPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            return normalized.EndsWith("/saves/current_run.save", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/saves/current_run.save.backup", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMultiplayerCurrentRunPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            return normalized.EndsWith("/saves/current_run_mp.save", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/saves/current_run_mp.save.backup", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetLocalSteamId(out ulong localSteamId)
        {
            localSteamId = default;

            try
            {
                var accountDirectory = new DirectoryInfo(_accountRoot).Name;
                return ulong.TryParse(accountDirectory, out localSteamId);
            }
            catch
            {
                return false;
            }
        }

        private bool TryNormalizeMultiplayerRunForTarget(
            string sourcePath,
            string targetPath,
            byte[] sourceContent,
            out byte[] normalizedContent,
            out string logMessage)
        {
            normalizedContent = sourceContent;
            logMessage = string.Empty;

            if (!IsMultiplayerCurrentRunPath(sourcePath))
            {
                return false;
            }

            if (!TryGetLocalSteamId(out var localSteamId))
            {
                return false;
            }

            if (!TryGetMultiplayerParticipantIds(sourceContent, out var participantIds) || participantIds.Count == 0)
            {
                return false;
            }

            if (!TryGetProfileIndexAndModeFromPath(targetPath, out var targetProfileIndex, out var targetVanillaMode))
            {
                return false;
            }

            var sourceProfileDir = TryGetProfileDirectory(sourcePath);
            if (string.IsNullOrEmpty(sourceProfileDir)
                || !TryGetSinglePlayerRunSignature(sourceProfileDir, out var localSignature))
            {
                return false;
            }

            if (targetVanillaMode)
            {
                if (participantIds.Contains(localSteamId))
                {
                    return false;
                }

                if (!TryMatchLocalMultiplayerPlayerId(sourceContent, localSignature, out var sourceLocalPlayerId))
                {
                    return false;
                }

                if (IsModdedPath(sourcePath) && sourceLocalPlayerId != localSteamId)
                {
                    BetterSavesConfig.SetModdedMultiplayerLocalPlayerId(targetProfileIndex, sourceLocalPlayerId);
                }

                if (!TryRewriteMultiplayerPlayerIds(sourceContent, sourceLocalPlayerId, localSteamId, out normalizedContent))
                {
                    normalizedContent = sourceContent;
                    return false;
                }

                logMessage =
                    $"Normalized multiplayer run IDs for vanilla target '{targetPath}' by rewriting local player ID " +
                    $"{sourceLocalPlayerId} -> {localSteamId}.";
                return true;
            }

            if (!participantIds.Contains(localSteamId))
            {
                return false;
            }

            if (!TryGetModdedMultiplayerLocalPlayerId(targetProfileIndex, localSignature, localSteamId, out var moddedLocalPlayerId)
                || moddedLocalPlayerId == 0
                || moddedLocalPlayerId == localSteamId)
            {
                return false;
            }

            if (!TryRewriteMultiplayerPlayerIds(sourceContent, localSteamId, moddedLocalPlayerId, out normalizedContent))
            {
                normalizedContent = sourceContent;
                return false;
            }

            logMessage =
                $"Normalized multiplayer run IDs for modded target '{targetPath}' by rewriting local player ID " +
                $"{localSteamId} -> {moddedLocalPlayerId}.";
            return true;
        }

        private bool TryGetModdedMultiplayerLocalPlayerId(
            int profileIndex,
            RunSignature localSignature,
            ulong localSteamId,
            out ulong moddedLocalPlayerId)
        {
            moddedLocalPlayerId = BetterSavesConfig.GetModdedMultiplayerLocalPlayerId(profileIndex);
            if (moddedLocalPlayerId != 0)
            {
                return true;
            }

            var moddedProfileDir = GetProfileDirectory(profileIndex, vanillaMode: false);
            var savesDir = Path.Combine(moddedProfileDir, "saves");
            if (!Directory.Exists(savesDir))
            {
                return false;
            }

            foreach (var path in Directory.EnumerateFiles(savesDir, "current_run_mp*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
            {
                if (!TryReadStableFile(path, out var content, out _)
                    || !TryGetMultiplayerParticipantIds(content, out var participantIds)
                    || participantIds.Count == 0
                    || participantIds.Contains(localSteamId))
                {
                    continue;
                }

                if (!TryMatchLocalMultiplayerPlayerId(content, localSignature, out moddedLocalPlayerId)
                    || moddedLocalPlayerId == 0
                    || moddedLocalPlayerId == localSteamId)
                {
                    continue;
                }

                BetterSavesConfig.SetModdedMultiplayerLocalPlayerId(profileIndex, moddedLocalPlayerId);
                return true;
            }

            moddedLocalPlayerId = 0;
            return false;
        }

        private static bool TryGetMultiplayerParticipantIds(byte[] content, out HashSet<ulong> participantIds)
        {
            participantIds = [];

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("players", out var playersElement)
                    || playersElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                foreach (var playerElement in playersElement.EnumerateArray())
                {
                    if (!playerElement.TryGetProperty("net_id", out var netIdElement))
                    {
                        continue;
                    }

                    if (TryGetUInt64(netIdElement, out var participantId))
                    {
                        participantIds.Add(participantId);
                    }
                }

                return participantIds.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetSinglePlayerRunSignature(string profileDir, out RunSignature signature)
        {
            signature = default;

            foreach (var path in GetCurrentRunPairPaths(profileDir, isMultiplayer: false))
            {
                if (!File.Exists(path) || !TryReadStableFile(path, out var content, out _))
                {
                    continue;
                }

                if (TryParseRunSignature(content, out signature))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryMatchLocalMultiplayerPlayerId(
            byte[] multiplayerRunContent,
            RunSignature localSignature,
            out ulong localPlayerId)
        {
            localPlayerId = default;

            try
            {
                using var document = JsonDocument.Parse(multiplayerRunContent);
                if (!document.RootElement.TryGetProperty("players", out var playersElement)
                    || playersElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                ulong bestPlayerId = default;
                var bestScore = int.MinValue;
                var bestCount = 0;

                foreach (var playerElement in playersElement.EnumerateArray())
                {
                    if (!playerElement.TryGetProperty("net_id", out var netIdElement)
                        || !TryGetUInt64(netIdElement, out var candidatePlayerId)
                        || !TryParseRunSignature(playerElement, out var candidateSignature))
                    {
                        continue;
                    }

                    var score = ScoreRunSignatureMatch(localSignature, candidateSignature);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPlayerId = candidatePlayerId;
                        bestCount = 1;
                    }
                    else if (score == bestScore)
                    {
                        bestCount++;
                    }
                }

                if (bestCount != 1 || bestScore < 10)
                {
                    return false;
                }

                localPlayerId = bestPlayerId;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRewriteMultiplayerPlayerIds(byte[] originalContent, ulong oldPlayerId, ulong newPlayerId, out byte[] rewrittenContent)
        {
            rewrittenContent = originalContent;

            try
            {
                var rootNode = JsonNode.Parse(originalContent);
                if (rootNode is null)
                {
                    return false;
                }

                var changed = RewriteMultiplayerPlayerIdsRecursive(rootNode, oldPlayerId, newPlayerId);
                if (!changed)
                {
                    return false;
                }

                rewrittenContent = Encoding.UTF8.GetBytes(rootNode.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool RewriteMultiplayerPlayerIdsRecursive(JsonNode node, ulong oldPlayerId, ulong newPlayerId)
        {
            var changed = false;

            switch (node)
            {
                case JsonObject obj:
                    foreach (var kvp in obj.ToList())
                    {
                        if (kvp.Value is null)
                        {
                            continue;
                        }

                        if ((string.Equals(kvp.Key, "net_id", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(kvp.Key, "player_id", StringComparison.OrdinalIgnoreCase))
                            && TryGetUInt64(kvp.Value, out var currentId)
                            && currentId == oldPlayerId)
                        {
                            obj[kvp.Key] = JsonValue.Create(newPlayerId);
                            changed = true;
                            continue;
                        }

                        changed |= RewriteMultiplayerPlayerIdsRecursive(kvp.Value, oldPlayerId, newPlayerId);
                    }

                    break;

                case JsonArray array:
                    foreach (var child in array)
                    {
                        if (child is null)
                        {
                            continue;
                        }

                        changed |= RewriteMultiplayerPlayerIdsRecursive(child, oldPlayerId, newPlayerId);
                    }

                    break;
            }

            return changed;
        }

        private static bool TryParseRunSignature(byte[] content, out RunSignature signature)
        {
            signature = default;

            try
            {
                using var document = JsonDocument.Parse(content);
                if (TryParseRunSignature(document.RootElement, out signature))
                {
                    return true;
                }

                if (document.RootElement.TryGetProperty("players", out var playersElement)
                    && playersElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var playerElement in playersElement.EnumerateArray())
                    {
                        if (TryParseRunSignature(playerElement, out signature))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseRunSignature(JsonElement element, out RunSignature signature)
        {
            signature = default;

            if (!element.TryGetProperty("character_id", out var characterIdElement)
                || !element.TryGetProperty("current_hp", out var currentHpElement)
                || !element.TryGetProperty("max_hp", out var maxHpElement)
                || !element.TryGetProperty("gold", out var goldElement)
                || !element.TryGetProperty("deck", out var deckElement)
                || characterIdElement.ValueKind != JsonValueKind.String
                || deckElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            signature = new RunSignature(
                characterIdElement.GetString() ?? string.Empty,
                currentHpElement.GetInt32(),
                maxHpElement.GetInt32(),
                goldElement.GetInt32(),
                deckElement.GetArrayLength());
            return !string.IsNullOrWhiteSpace(signature.CharacterId);
        }

        private static int ScoreRunSignatureMatch(RunSignature localSignature, RunSignature candidateSignature)
        {
            var score = 0;

            if (string.Equals(localSignature.CharacterId, candidateSignature.CharacterId, StringComparison.Ordinal))
            {
                score += 8;
            }

            if (localSignature.CurrentHp == candidateSignature.CurrentHp)
            {
                score += 4;
            }

            if (localSignature.MaxHp == candidateSignature.MaxHp)
            {
                score += 4;
            }

            if (localSignature.Gold == candidateSignature.Gold)
            {
                score += 2;
            }

            if (localSignature.DeckCount == candidateSignature.DeckCount)
            {
                score += 2;
            }

            return score;
        }

        private static bool TryGetUInt64(JsonNode node, out ulong value)
        {
            value = default;

            return node switch
            {
                JsonValue jsonValue when jsonValue.TryGetValue<ulong>(out value) => true,
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) && ulong.TryParse(text, out value) => true,
                _ => false
            };
        }

        private static bool TryGetUInt64(JsonElement element, out ulong value)
        {
            value = default;

            return element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetUInt64(out value),
                JsonValueKind.String => ulong.TryParse(element.GetString(), out value),
                _ => false
            };
        }

        private readonly record struct RunSignature(
            string CharacterId,
            int CurrentHp,
            int MaxHp,
            int Gold,
            int DeckCount);

        private SinglePlayerDataState GetSinglePlayerDataState(string profileDir)
        {
            var savesDir = Path.Combine(profileDir, "saves");
            var progressSavePath = Path.Combine(savesDir, "progress.save");
            var prefsSavePath = Path.Combine(savesDir, "prefs.save");
            var historyDir = Path.Combine(savesDir, "history");

            return new SinglePlayerDataState(
                GetFileLengthSafe(progressSavePath),
                GetTopLevelJsonArrayEntryCount(progressSavePath),
                CountPrimaryHistoryEntries(historyDir),
                GetCurrentRunPairPaths(profileDir, isMultiplayer: false).Any(File.Exists),
                GetCurrentRunPairPaths(profileDir, isMultiplayer: true).Any(File.Exists),
                GetFileLengthSafe(prefsSavePath));
        }

        private static ProgressSemanticState GetProgressSemanticState(string path)
        {
            if (!TryReadStableFile(path, out var content, out _))
            {
                return default;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return default;
                }

                var revealedEpochs = 0;
                var obtainedEpochs = 0;
                var revealedEpochIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var obtainedEpochIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.TryGetProperty("epochs", out var epochsElement)
                    && epochsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var epochElement in epochsElement.EnumerateArray())
                    {
                        var epochId = epochElement.TryGetProperty("id", out var epochIdElement)
                                      && epochIdElement.ValueKind == JsonValueKind.String
                            ? epochIdElement.GetString() ?? string.Empty
                            : string.Empty;

                        if (!epochElement.TryGetProperty("state", out var stateElement)
                            || stateElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var state = stateElement.GetString();
                        if (string.Equals(state, "revealed", StringComparison.OrdinalIgnoreCase))
                        {
                            revealedEpochs++;
                            if (!string.IsNullOrWhiteSpace(epochId))
                            {
                                revealedEpochIds.Add(epochId);
                            }
                        }
                        else if (string.Equals(state, "obtained", StringComparison.OrdinalIgnoreCase))
                        {
                            obtainedEpochs++;
                            if (!string.IsNullOrWhiteSpace(epochId))
                            {
                                obtainedEpochIds.Add(epochId);
                            }
                        }
                    }
                }

                var characterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.TryGetProperty("character_stats", out var characterStatsElement)
                    && characterStatsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var characterStatElement in characterStatsElement.EnumerateArray())
                    {
                        if (!characterStatElement.TryGetProperty("id", out var characterIdElement)
                            || characterIdElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var characterId = characterIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(characterId))
                        {
                            characterIds.Add(characterId);
                        }
                    }
                }

                var totalUnlocks = document.RootElement.TryGetProperty("total_unlocks", out var totalUnlocksElement)
                    ? totalUnlocksElement.ValueKind switch
                    {
                        JsonValueKind.Number => totalUnlocksElement.GetInt32(),
                        JsonValueKind.String when int.TryParse(totalUnlocksElement.GetString(), out var parsed) => parsed,
                        _ => 0
                    }
                    : 0;

                var achievementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.TryGetProperty("unlocked_achievements", out var achievementsElement)
                    && achievementsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var achievementElement in achievementsElement.EnumerateArray())
                    {
                        if (achievementElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var achievementId = achievementElement.GetString();
                        if (!string.IsNullOrWhiteSpace(achievementId))
                        {
                            achievementIds.Add(achievementId);
                        }
                    }
                }

                return new ProgressSemanticState(
                    revealedEpochs,
                    obtainedEpochs,
                    revealedEpochIds,
                    obtainedEpochIds,
                    characterIds,
                    totalUnlocks,
                    achievementIds);
            }
            catch
            {
                return default;
            }
        }

        private static long GetFileLengthSafe(string path)
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static int CountPrimaryHistoryEntries(string historyDir)
        {
            if (!Directory.Exists(historyDir))
            {
                return 0;
            }

            try
            {
                return Directory.EnumerateFiles(historyDir, "*.run", SearchOption.TopDirectoryOnly).Count();
            }
            catch
            {
                return 0;
            }
        }

        private static int GetTopLevelJsonArrayEntryCount(string path)
        {
            if (!TryReadStableFile(path, out var content, out _))
            {
                return 0;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return 0;
                }

                var count = 0;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        count += property.Value.GetArrayLength();
                    }
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        private string? TryGetProfileDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(_accountRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var relativePath = Path.GetRelativePath(_accountRoot, fullPath);
                var segments = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2)
                {
                    return null;
                }

                var segmentIndex = 0;
                var pathSegments = new List<string>();

                if (segments[0].Equals("modded", StringComparison.OrdinalIgnoreCase))
                {
                    if (segments.Length < 3)
                    {
                        return null;
                    }

                    pathSegments.Add("modded");
                    segmentIndex = 1;
                }

                if (!IsTrackedProfileDirectory(segments[segmentIndex]))
                {
                    return null;
                }

                pathSegments.Add(segments[segmentIndex]);
                return Path.Combine(_accountRoot, Path.Combine(pathSegments.ToArray()));
            }
            catch
            {
                return null;
            }
        }

        private bool TryGetProfileIndexAndModeFromPath(string path, out int profileIndex, out bool vanillaMode)
        {
            profileIndex = default;
            vanillaMode = true;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(_accountRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var relativePath = Path.GetRelativePath(_accountRoot, fullPath);
                var segments = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2)
                {
                    return false;
                }

                var segmentIndex = 0;
                vanillaMode = true;

                if (segments[0].Equals("modded", StringComparison.OrdinalIgnoreCase))
                {
                    if (segments.Length < 3)
                    {
                        return false;
                    }

                    vanillaMode = false;
                    segmentIndex = 1;
                }

                if (!segments[segmentIndex].StartsWith("profile", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!int.TryParse(segments[segmentIndex]["profile".Length..], out profileIndex))
                {
                    return false;
                }

                return profileIndex is >= 1 and <= 3;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetCurrentRunPairPaths(string profileDirectory, bool isMultiplayer)
        {
            if (string.IsNullOrWhiteSpace(profileDirectory))
            {
                return Array.Empty<string>();
            }

            var savesDirectory = Path.Combine(profileDirectory, "saves");
            return isMultiplayer
                ?
                [
                    Path.Combine(savesDirectory, "current_run_mp.save"),
                    Path.Combine(savesDirectory, "current_run_mp.save.backup")
                ]
                :
                [
                    Path.Combine(savesDirectory, "current_run.save"),
                    Path.Combine(savesDirectory, "current_run.save.backup")
                ];
        }

        private static bool TryGetCurrentRunStartTime(
            string firstProfileDir,
            string secondProfileDir,
            bool isMultiplayer,
            out long startTime)
        {
            startTime = default;

            foreach (var path in GetCurrentRunPairPaths(firstProfileDir, isMultiplayer)
                         .Concat(GetCurrentRunPairPaths(secondProfileDir, isMultiplayer))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (TryGetCurrentRunStartTime(path, out startTime))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetCurrentRunStartTime(string path, out long startTime)
        {
            startTime = default;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!TryReadStableFile(path, out var content, out _))
            {
                return false;
            }

            return TryGetCurrentRunStartTime(content, out startTime);
        }

        private static bool TryGetCurrentRunStartTime(byte[] content, out long startTime)
        {
            startTime = default;

            if (content.Length == 0)
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("start_time", out var startTimeElement))
                {
                    return false;
                }

                startTime = startTimeElement.ValueKind switch
                {
                    JsonValueKind.Number => startTimeElement.GetInt64(),
                    JsonValueKind.String when long.TryParse(startTimeElement.GetString(), out var parsed) => parsed,
                    _ => default
                };

                return startTime != default;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetHistoryEntryPathsForStartTime(
            string firstProfileDir,
            string secondProfileDir,
            long startTime,
            bool isMultiplayer)
        {
            if (isMultiplayer)
            {
                return Array.Empty<string>();
            }

            return new[]
            {
                Path.Combine(firstProfileDir, "saves", "history", $"{startTime}.run"),
                Path.Combine(secondProfileDir, "saves", "history", $"{startTime}.run")
            };
        }

        private void DeleteFileSafely(string targetPath, string reason)
        {
            if (!File.Exists(targetPath))
            {
                return;
            }

            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        SuppressPath(targetPath);
                        File.Delete(targetPath);
                        RecordContentMutation();
                        CloudMirrorService.DeleteFile(_accountRoot, targetPath);
                        Log.Info($"[BetterSaves] Deleted '{targetPath}' ({reason}).");
                        return;
                    }
                    catch (IOException) when (attempt < 7)
                    {
                        Thread.Sleep(100);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 7)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to delete '{targetPath}' ({reason}): {ex}");
            }
        }

        private bool IsSuppressed(string path)
        {
            if (!_suppressedPaths.TryGetValue(path, out var expiration))
            {
                return false;
            }

            if (expiration >= DateTime.UtcNow)
            {
                return true;
            }

            _suppressedPaths.TryRemove(path, out _);
            return false;
        }

        private void SuppressPath(string path)
        {
            _suppressedPaths[path] = DateTime.UtcNow.AddSeconds(2);
        }

        private static bool TryReadStableFile(
            string sourcePath,
            out byte[] content,
            out DateTime lastWriteTimeUtc)
        {
            content = Array.Empty<byte>();
            lastWriteTimeUtc = default;

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (!File.Exists(sourcePath))
                {
                    return false;
                }

                try
                {
                    var before = new FileInfo(sourcePath);
                    using var sourceStream = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var buffer = new MemoryStream();
                    sourceStream.CopyTo(buffer);

                    var after = new FileInfo(sourcePath);
                    if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc)
                    {
                        content = buffer.ToArray();
                        lastWriteTimeUtc = after.LastWriteTimeUtc;
                        return true;
                    }
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(250);
                    continue;
                }
                catch (UnauthorizedAccessException) when (attempt < 19)
                {
                    Thread.Sleep(250);
                    continue;
                }

                Thread.Sleep(250);
            }

            return false;
        }

        private static bool TargetAlreadyMatches(string targetPath, long sourceLength, DateTime sourceWriteTimeUtc)
        {
            var targetSnapshot = FileSnapshot.TryCreate(targetPath);
            return targetSnapshot is not null
                && targetSnapshot.Value.Length == sourceLength
                && targetSnapshot.Value.LastWriteTimeUtc == sourceWriteTimeUtc;
        }

        private static void WriteFileWithRetries(string targetPath, byte[] content, DateTime lastWriteTimeUtc)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (var targetStream = new FileStream(
                               targetPath,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.Read))
                    {
                        targetStream.Write(content, 0, content.Length);
                    }

                    File.SetLastWriteTimeUtc(targetPath, lastWriteTimeUtc);
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException) when (attempt < 19)
                {
                    Thread.Sleep(250);
                }
            }
        }
    }

    internal enum BootstrapImportAction
    {
        None = 0,
        VanillaToModded = 1,
        ModdedToVanilla = 2
    }

    internal enum BootstrapPromptKind
    {
        ConfirmSingleImport = 0,
        ChooseAuthoritativeSide = 1
    }

    internal readonly record struct BootstrapPromptRequest(
        string AccountRoot,
        int ProfileIndex,
        BootstrapPromptKind Kind,
        BootstrapImportAction Action,
        string Reason);

    private sealed record GuardedFileSnapshot(
        string Path,
        byte[] Content,
        DateTime LastWriteTimeUtc);

    private sealed record DataOnlyCloudSyncGuard(
        AccountSyncRoot Root,
        int ProfileIndex,
        bool VanillaMode,
        IReadOnlyList<GuardedFileSnapshot> Files);

    private readonly record struct BootstrapDecision(
        bool ShouldHandle,
        BootstrapPromptKind? PromptKind,
        BootstrapImportAction? Action,
        string? LogMessage)
    {
        public static BootstrapDecision None()
        {
            return new BootstrapDecision(false, null, null, null);
        }

        public static BootstrapDecision Block()
        {
            return new BootstrapDecision(true, null, null, null);
        }

        public static BootstrapDecision Prompt(
            BootstrapPromptKind promptKind,
            BootstrapImportAction action,
            string logMessage)
        {
            return new BootstrapDecision(true, promptKind, action, logMessage);
        }
    }

    private enum SourceSide
    {
        None,
        First,
        Second
    }

    private readonly record struct FileSnapshot(long Length, DateTime LastWriteTimeUtc)
    {
        public static FileSnapshot? TryCreate(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            return new FileSnapshot(fileInfo.Length, fileInfo.LastWriteTimeUtc);
        }
    }

    private readonly record struct RecentProcessedChange(
        long Length,
        DateTime LastWriteTimeUtc,
        DateTime ExpiresUtc);

    private readonly record struct SinglePlayerDataState(
        long ProgressLength,
        int ProgressCollectionScore,
        int HistoryEntryCount,
        bool HasSinglePlayerCurrentRun,
        bool HasMultiplayerCurrentRun,
        long PrefsLength)
    {
        public bool HasAnySinglePlayerData =>
            ProgressLength > 0
            || PrefsLength > 0
            || HistoryEntryCount > 0
            || HasSinglePlayerCurrentRun
            || HasMultiplayerCurrentRun;

        public long Score =>
            ProgressLength
            + (PrefsLength * 4L)
            + (ProgressCollectionScore * 512L)
            + (HistoryEntryCount * 4096L)
            + (HasSinglePlayerCurrentRun ? 131072L : 0L);

        public bool IsLowDataSinglePlayerProfile =>
            !HasSinglePlayerCurrentRun
            && HistoryEntryCount <= 4
            && ProgressLength <= 16 * 1024
            && ProgressCollectionScore <= 96
            && PrefsLength <= 1024;

        public bool IsEstablishedSinglePlayerProfile =>
            HasSinglePlayerCurrentRun
            || HistoryEntryCount >= 12
            || ProgressLength >= 24 * 1024
            || (ProgressLength >= 12 * 1024 && ProgressCollectionScore >= 96);

        public bool IsMeaningfullyRicherThan(SinglePlayerDataState other)
        {
            if (!IsEstablishedSinglePlayerProfile)
            {
                return false;
            }

            if (!other.IsLowDataSinglePlayerProfile)
            {
                return false;
            }

            return HasSinglePlayerCurrentRun
                || HistoryEntryCount > other.HistoryEntryCount
                || ProgressCollectionScore >= Math.Max(48, other.ProgressCollectionScore * 2)
                || ProgressLength >= Math.Max(24 * 1024, Math.Max(other.ProgressLength * 2, other.ProgressLength + 8 * 1024));
        }

        public bool IsClearlyRicherThan(SinglePlayerDataState other)
        {
            if (!HasAnySinglePlayerData)
            {
                return false;
            }

            if (!other.HasAnySinglePlayerData)
            {
                return true;
            }

            return Score >= Math.Max(24 * 1024L, other.Score * 2L)
                && (HasSinglePlayerCurrentRun
                    || HistoryEntryCount > other.HistoryEntryCount
                    || ProgressLength > other.ProgressLength
                    || ProgressCollectionScore > other.ProgressCollectionScore);
        }

        public override string ToString()
        {
            return
                $"progress_len={ProgressLength}, progress_score={ProgressCollectionScore}, history={HistoryEntryCount}, " +
                $"sp_run={HasSinglePlayerCurrentRun}, mp_run={HasMultiplayerCurrentRun}, prefs_len={PrefsLength}, score={Score}";
        }
    }

    private readonly record struct ProgressSemanticState(
        int RevealedEpochCount,
        int ObtainedEpochCount,
        HashSet<string> RevealedEpochIds,
        HashSet<string> ObtainedEpochIds,
        HashSet<string> CharacterIds,
        int TotalUnlocks,
        HashSet<string> AchievementIds)
    {
        private int CharacterCount => CharacterIds?.Count ?? 0;
        private int AchievementCount => AchievementIds?.Count ?? 0;
        private int RevealedEpochIdCount => RevealedEpochIds?.Count ?? 0;
        private int ObtainedEpochIdCount => ObtainedEpochIds?.Count ?? 0;

        public long Score =>
            (ObtainedEpochCount * 10L)
            + (RevealedEpochCount * 6L)
            + (CharacterCount * 8L)
            + (TotalUnlocks * 4L)
            + AchievementCount;

        public bool IsMeaningfullyRicherThan(ProgressSemanticState other)
        {
            if (Score == 0)
            {
                return false;
            }

            if (other.Score == 0)
            {
                return true;
            }

            var strictlyContainsOtherSemanticProgress =
                ContainsAll(RevealedEpochIds, other.RevealedEpochIds)
                && ContainsAll(ObtainedEpochIds, other.ObtainedEpochIds)
                && ContainsAll(CharacterIds, other.CharacterIds)
                && ContainsAll(AchievementIds, other.AchievementIds)
                && (RevealedEpochIdCount > other.RevealedEpochIdCount
                    || ObtainedEpochIdCount > other.ObtainedEpochIdCount
                    || CharacterCount > other.CharacterCount
                    || AchievementCount > other.AchievementCount
                    || TotalUnlocks > other.TotalUnlocks);

            if (strictlyContainsOtherSemanticProgress)
            {
                return true;
            }

            return Score > other.Score
                && (ObtainedEpochCount > other.ObtainedEpochCount
                    || RevealedEpochCount > other.RevealedEpochCount
                    || CharacterCount > other.CharacterCount
                    || TotalUnlocks > other.TotalUnlocks
                    || AchievementCount > other.AchievementCount);
        }

        private static bool ContainsAll(HashSet<string>? candidate, HashSet<string>? other)
        {
            if (other is null || other.Count == 0)
            {
                return true;
            }

            if (candidate is null || candidate.Count == 0)
            {
                return false;
            }

            return other.All(candidate.Contains);
        }

        public override string ToString()
        {
            return
                $"revealed_epochs={RevealedEpochCount}, obtained_epochs={ObtainedEpochCount}, " +
                $"characters={CharacterCount}, total_unlocks={TotalUnlocks}, achievements={AchievementCount}, score={Score}";
        }
    }
}
