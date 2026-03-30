using System.Collections.Concurrent;
using System.Text.Json;
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
            "saves/current_run.save.backup"
        };

    private static readonly HashSet<string> DataOnlyExactPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/progress.save",
            "saves/progress.save.backup"
        };

    private static readonly string[] DataOnlyPrefixes =
    [
        "saves/history/",
        "saves/replays/"
    ];

    private static readonly HashSet<string> IgnoredPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/current_run_mp.save",
            "saves/current_run_mp.save.backup"
        };

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
    private static readonly List<AccountSyncRoot> SyncRoots = [];
    private static readonly CancellationTokenSource ReconcileCancellation = new();
    private static readonly SemaphoreSlim ImmediateReconcileSemaphore = new(1, 1);
    private static bool _initialized;

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

            foreach (var accountRoot in Directory.EnumerateDirectories(steamRoot))
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

    public static void RequestImmediateReconcile(
        string reason,
        ReconcilePreference preference = ReconcilePreference.Auto)
    {
        if (!_initialized)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, ReconcileCancellation.Token);
                await ImmediateReconcileSemaphore.WaitAsync(ReconcileCancellation.Token);

                try
                {
                    ReconcileAllProfilesUnsafe(reason, preference);
                }
                finally
                {
                    ImmediateReconcileSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Immediate reconcile failed ({reason}): {ex}");
            }
        });
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

    public static void RecordPreferredProfileSelectionForCurrentMode(string reason)
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
                syncRoot.RecordPreferredProfileSelection(vanillaMode, reason);
            }
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

        public void RecordPreferredProfileSelection(bool vanillaMode, string reason)
        {
            var currentProfileId = TryGetActiveProfileIndex();
            if (currentProfileId is null)
            {
                return;
            }

            if (BetterSavesConfig.UsesSharedProfileSelection)
            {
                BetterSavesConfig.SetPreferredProfileId(true, currentProfileId.Value);
                BetterSavesConfig.SetPreferredProfileId(false, currentProfileId.Value);
                Log.Info(
                    $"[BetterSaves] Recorded shared preferred profile {currentProfileId.Value} " +
                    $"from {(vanillaMode ? "vanilla" : "modded")} activity ({reason}).");
            }
            else
            {
                BetterSavesConfig.SetPreferredProfileId(vanillaMode, currentProfileId.Value);
                Log.Info(
                    $"[BetterSaves] Recorded {(vanillaMode ? "vanilla" : "modded")} preferred profile {currentProfileId.Value} " +
                    $"({reason}).");
            }
        }

        public void RestorePreferredProfileSelection(bool vanillaMode)
        {
            var currentProfileId = TryGetActiveProfileIndex();
            if (currentProfileId is null)
            {
                return;
            }

            if (BetterSavesConfig.CurrentMode == SyncMode.SaveOnly
                && TryRealignSaveOnlyProfileSelection(vanillaMode, currentProfileId.Value))
            {
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

        private bool TryRealignSaveOnlyProfileSelection(bool vanillaMode, int currentProfileId)
        {
            if (!HasSinglePlayerCurrentRun(currentProfileId, vanillaMode))
            {
                return false;
            }

            var currentScore = GetProfileProgressScore(currentProfileId, vanillaMode);
            var recoveryCandidate = Enumerable.Range(1, 3)
                .Where(index => index != currentProfileId)
                .Select(index => new
                {
                    ProfileIndex = index,
                    Score = GetProfileProgressScore(index, vanillaMode)
                })
                .Where(entry => entry.Score >= 16384 && entry.Score >= Math.Max(1, currentScore) * 4)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.ProfileIndex)
                .FirstOrDefault();

            if (recoveryCandidate is null)
            {
                return false;
            }

            CopyCurrentRunBetweenProfiles(currentProfileId, recoveryCandidate.ProfileIndex, vanillaMode);
            CopyCurrentRunBetweenProfiles(currentProfileId, recoveryCandidate.ProfileIndex, !vanillaMode);

            SetActiveProfileIndex(
                recoveryCandidate.ProfileIndex,
                $"realigning save-only profile selection from low-data slot {currentProfileId}");

            BetterSavesConfig.SetPreferredProfileId(true, recoveryCandidate.ProfileIndex);
            BetterSavesConfig.SetPreferredProfileId(false, recoveryCandidate.ProfileIndex);
            Log.Info(
                $"[BetterSaves] Save-only realignment selected profile {recoveryCandidate.ProfileIndex} " +
                $"over low-data slot {currentProfileId} (score {currentScore} vs {recoveryCandidate.Score}).");
            return true;
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
            var targetRoot = preference switch
            {
                ReconcilePreference.VanillaToModded => moddedProfileDir,
                ReconcilePreference.ModdedToVanilla => vanillaProfileDir,
                _ => null
            };

            if (targetRoot is null)
            {
                Log.Info($"[BetterSaves] Skipped run deletion propagation for unsupported preference '{preference}' ({reason}).");
                return;
            }

            var fileNames = isMultiplayer
                ? new[] { "current_run_mp.save", "current_run_mp.save.backup" }
                : new[] { "current_run.save", "current_run.save.backup" };

            foreach (var fileName in fileNames)
            {
                var targetPath = Path.Combine(targetRoot, "saves", fileName);
                DeleteFileSafely(targetPath, reason);
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

            if (!Directory.Exists(vanillaProfileDir) && !Directory.Exists(moddedProfileDir))
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
                Log.Info(
                    $"[BetterSaves] Set active profile to {profileIndex} under '{_accountRoot}' " +
                    $"({reason}; previous profile was {currentProfileId}).");
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to set active profile under '{_accountRoot}' ({reason}): {ex}");
            }
        }

        private long GetProfileProgressScore(int profileIndex, bool vanillaMode)
        {
            var profileDir = GetProfileDirectory(profileIndex, vanillaMode);
            var progressSavePath = Path.Combine(profileDir, "saves", "progress.save");

            if (!File.Exists(progressSavePath))
            {
                return 0;
            }

            try
            {
                return new FileInfo(progressSavePath).Length;
            }
            catch
            {
                return 0;
            }
        }

        private bool HasSinglePlayerCurrentRun(int profileIndex, bool vanillaMode)
        {
            var profileDir = GetProfileDirectory(profileIndex, vanillaMode);
            return GetCurrentRunPairPaths(profileDir, isMultiplayer: false).Any(File.Exists);
        }

        private void CopyCurrentRunBetweenProfiles(int sourceProfileIndex, int targetProfileIndex, bool vanillaMode)
        {
            if (sourceProfileIndex == targetProfileIndex)
            {
                return;
            }

            var sourceProfileDir = GetProfileDirectory(sourceProfileIndex, vanillaMode);
            var targetProfileDir = GetProfileDirectory(targetProfileIndex, vanillaMode);

            foreach (var fileName in new[] { "current_run.save", "current_run.save.backup" })
            {
                var sourcePath = Path.Combine(sourceProfileDir, "saves", fileName);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var targetPath = Path.Combine(targetProfileDir, "saves", fileName);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                CopyFileSafely(sourcePath, targetPath, "save-only profile realignment");
            }
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

            var preference = VanillaModeCompatibilityPatches.CurrentReconcilePreference;
            if (!ShouldProcessPathForPreference(eventArgs.FullPath, preference))
            {
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

            var preference = VanillaModeCompatibilityPatches.CurrentReconcilePreference;
            if (!ShouldProcessPathForPreference(eventArgs.FullPath, preference))
            {
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

            var preference = VanillaModeCompatibilityPatches.CurrentReconcilePreference;
            if (!ShouldProcessPathForPreference(eventArgs.FullPath, preference))
            {
                return;
            }

            if (!IsSyncablePath(eventArgs.FullPath))
            {
                return;
            }

            if (!IsSuppressed(eventArgs.OldFullPath)
                && TryGetCounterpartPath(eventArgs.OldFullPath, out var oldCounterpartPath))
            {
                DeleteFileSafely(oldCounterpartPath, "Renamed");
            }

            OnFileChanged(sender, eventArgs);
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
            if (IgnoredPaths.Contains(normalized))
            {
                return false;
            }

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

        private static bool IsEphemeralProfileRelativePath(string normalizedProfileRelativePath)
        {
            return normalizedProfileRelativePath.Contains(".tmp", StringComparison.OrdinalIgnoreCase)
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

        private void CopyFileSafely(string sourcePath, string targetPath, string reason)
        {
            try
            {
                if (TryPurgeStaleCurrentRunPair(sourcePath, targetPath, reason))
                {
                    return;
                }

                if (!TryReadStableFile(sourcePath, out var content, out var lastWriteTimeUtc))
                {
                    return;
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
                CloudMirrorService.MirrorFile(_accountRoot, targetPath);
                MaybeRecordPreferredProfileFromPath(sourcePath, reason);

                Log.Info($"[BetterSaves] Mirrored '{sourcePath}' to '{targetPath}' ({reason}).");
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to mirror '{sourcePath}' to '{targetPath}' ({reason}): {ex}");
            }
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
}
