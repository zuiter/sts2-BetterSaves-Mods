using MegaCrit.Sts2.Core.Logging;

namespace BetterSaves;

internal static class FirstSyncBackupService
{
    private static readonly string[] RootFiles =
    [
        "profile.save",
        "settings.save"
    ];

    private static readonly string[] ProfileDirectories =
    [
        "profile1",
        "profile2",
        "profile3",
        Path.Combine("modded", "profile1"),
        Path.Combine("modded", "profile2"),
        Path.Combine("modded", "profile3")
    ];

    public static bool EnsureBackup(IEnumerable<string> accountRoots)
    {
        if (!BetterSavesConfig.IsBootstrapPending || BetterSavesConfig.IsBootstrapBackupCreated)
        {
            return true;
        }

        try
        {
            var backupRoot = CreateBackupRoot();

            foreach (var accountRoot in accountRoots)
            {
                BackupAccountRoot(accountRoot, backupRoot);
            }

            BetterSavesConfig.MarkBootstrapBackupCreated(backupRoot);
            Log.Info($"[BetterSaves] Created first-sync backup at '{backupRoot}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"[BetterSaves] Failed to create first-sync backup: {ex}");
            return false;
        }
    }

    private static string CreateBackupRoot()
    {
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2",
            "mods",
            "BetterSaves",
            "backups",
            $"first-sync-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backupRoot);
        return backupRoot;
    }

    private static void BackupAccountRoot(string accountRoot, string backupRoot)
    {
        var accountDirectory = new DirectoryInfo(accountRoot).Name;
        var destinationAccountRoot = Path.Combine(backupRoot, accountDirectory);
        Directory.CreateDirectory(destinationAccountRoot);

        foreach (var fileName in RootFiles)
        {
            CopyFileIfExists(
                Path.Combine(accountRoot, fileName),
                Path.Combine(destinationAccountRoot, fileName));
        }

        foreach (var relativeDirectory in ProfileDirectories)
        {
            CopyDirectoryIfExists(
                Path.Combine(accountRoot, relativeDirectory),
                Path.Combine(destinationAccountRoot, relativeDirectory));
        }
    }

    private static void CopyDirectoryIfExists(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            CopyFileIfExists(filePath, destinationPath);
        }
    }

    private static void CopyFileIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }
}
