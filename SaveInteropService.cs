using System.Collections.Concurrent;
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

    private static readonly HashSet<string> CurrentRunOnlyPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/current_run.save",
            "saves/current_run.save.backup"
        };

    private static readonly HashSet<string> IgnoredPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "saves/current_run_mp.save",
            "saves/current_run_mp.save.backup"
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

        private void SyncProfilePair(int profileIndex, string reason, ReconcilePreference preference)
        {
            var vanillaProfileDir = Path.Combine(_accountRoot, $"profile{profileIndex}");
            var moddedProfileDir = Path.Combine(_accountRoot, "modded", $"profile{profileIndex}");

            if (!Directory.Exists(vanillaProfileDir) && !Directory.Exists(moddedProfileDir))
            {
                return;
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

            if (ShouldIgnoreDeletion(eventArgs.FullPath))
            {
                return;
            }

            if (!TryGetCounterpartPath(eventArgs.FullPath, out var counterpartPath))
            {
                return;
            }

            DeleteFileSafely(counterpartPath, eventArgs.ChangeType.ToString());
        }

        private static bool ShouldIgnoreDeletion(string sourcePath)
        {
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

            return BetterSavesConfig.IsFullSyncEnabled || CurrentRunOnlyPaths.Contains(normalized);
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

                Log.Info($"[BetterSaves] Mirrored '{sourcePath}' to '{targetPath}' ({reason}).");
            }
            catch (Exception ex)
            {
                Log.Info($"[BetterSaves] Failed to mirror '{sourcePath}' to '{targetPath}' ({reason}): {ex}");
            }
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
