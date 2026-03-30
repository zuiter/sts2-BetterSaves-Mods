using MegaCrit.Sts2.Core.Logging;
using Steamworks;

namespace BetterSaves;

internal static class CloudMirrorService
{
    public static void MirrorFile(string accountRoot, string targetPath)
    {
        try
        {
            if (!File.Exists(targetPath))
            {
                return;
            }

            var relativePath = TryGetCloudRelativePath(accountRoot, targetPath);
            if (relativePath is null)
            {
                return;
            }

            if (ShouldSkipCloudMirror(relativePath))
            {
                return;
            }

            var bytes = File.ReadAllBytes(targetPath);
            if (!SteamRemoteStorage.FileWrite(relativePath, bytes, bytes.Length))
            {
                Log.Info($"[BetterSaves] Failed to mirror cloud file '{relativePath}'.");
                return;
            }

            Log.Info($"[BetterSaves] Mirrored cloud file '{relativePath}'.");
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to mirror cloud counterpart for '{targetPath}': {ex}");
        }
    }

    public static void DeleteFile(string accountRoot, string targetPath)
    {
        try
        {
            var relativePath = TryGetCloudRelativePath(accountRoot, targetPath);
            if (relativePath is null)
            {
                return;
            }

            if (ShouldSkipCloudMirror(relativePath))
            {
                return;
            }

            if (!SteamRemoteStorage.FileExists(relativePath))
            {
                return;
            }

            if (!SteamRemoteStorage.FileDelete(relativePath))
            {
                Log.Info($"[BetterSaves] Failed to delete cloud file '{relativePath}'.");
                return;
            }

            Log.Info($"[BetterSaves] Deleted cloud file '{relativePath}'.");
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to delete cloud counterpart for '{targetPath}': {ex}");
        }
    }

    private static string? TryGetCloudRelativePath(string accountRoot, string targetPath)
    {
        string fullAccountRoot;
        string fullTargetPath;
        try
        {
            fullAccountRoot = Path.GetFullPath(accountRoot);
            fullTargetPath = Path.GetFullPath(targetPath);
        }
        catch
        {
            return null;
        }

        if (!fullTargetPath.StartsWith(fullAccountRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(fullAccountRoot, fullTargetPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        return relativePath.Replace('\\', '/');
    }

    private static bool ShouldSkipCloudMirror(string relativePath)
    {
        if (BetterSavesConfig.CurrentMode != SyncMode.DataOnly)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        return normalized.Contains("/saves/history/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/saves/replays/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/saves/progress.save", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/saves/progress.save.backup", StringComparison.OrdinalIgnoreCase);
    }
}
