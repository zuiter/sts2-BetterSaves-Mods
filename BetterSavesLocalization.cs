using Godot;

namespace BetterSaves;

internal static class BetterSavesLocalization
{
    public static bool IsChinese()
    {
        var locale = TranslationServer.GetLocale()?.ToLowerInvariant() ?? string.Empty;
        return locale.StartsWith("zh", StringComparison.Ordinal)
            || locale.StartsWith("zhs", StringComparison.Ordinal)
            || locale.StartsWith("zht", StringComparison.Ordinal);
    }

    public static string GetPanelTitle()
    {
        return IsChinese() ? "BetterSaves互通模式" : "BetterSaves Interop Mode";
    }

    public static string GetPanelDescription()
    {
        return IsChinese()
            ? "选择 BetterSaves 的互通范围。仅同步存档只同步当前局存档；仅同步数据只同步时间线、发现与历史等数据；完整同步会同步两者。仅同步数据模式不会把镜像结果额外写回 Steam 云。"
            : "Choose what BetterSaves syncs. Save Only mirrors run saves only; Data Only mirrors timeline, discoveries, and history; Full Sync mirrors both. Data Only does not push mirrored data back into Steam Cloud.";
    }

    public static string GetModeDisplayName(SyncMode mode)
    {
        if (IsChinese())
        {
            return mode switch
            {
                SyncMode.SaveOnly => "仅同步存档",
                SyncMode.DataOnly => "仅同步数据",
                SyncMode.FullSync => "完整同步",
                _ => mode.ToString()
            };
        }

        return mode switch
        {
            SyncMode.SaveOnly => "Save Only",
            SyncMode.DataOnly => "Data Only",
            SyncMode.FullSync => "Full Sync",
            _ => mode.ToString()
        };
    }

    public static string GetModeDescription(SyncMode mode)
    {
        if (IsChinese())
        {
            return mode switch
            {
                SyncMode.SaveOnly => "只同步当前进行中的存档文件，不同步时间线、发现、历史记录和设置。",
                SyncMode.DataOnly => "只同步时间线、发现、历史记录等数据，不同步当前局存档文件，也不会把镜像后的数据额外写回 Steam 云。",
                SyncMode.FullSync => "同步存档文件与数据两部分内容，包括进度、历史记录和回放文件。",
                _ => string.Empty
            };
        }

        return mode switch
        {
            SyncMode.SaveOnly =>
                "Syncs only the in-progress run save files and leaves progression, timeline, history, and settings alone.",
            SyncMode.DataOnly =>
                "Syncs timeline, discoveries, and history data, but does not sync the in-progress run save files or push mirrored data back into Steam Cloud.",
            SyncMode.FullSync =>
                "Syncs both save files and profile data, including progression, history, and replay files.",
            _ => string.Empty
        };
    }

    public static string GetActiveSaveTypeBadgeText()
    {
        var isVanilla = VanillaModeCompatibilityPatches.IsCompatibilityModeEnabled;
        if (IsChinese())
        {
            return isVanilla ? "原版存档" : "模组存档";
        }

        return isVanilla ? "Vanilla Save" : "Modded Save";
    }

    public static Color GetActiveSaveTypeBadgeColor()
    {
        return VanillaModeCompatibilityPatches.IsCompatibilityModeEnabled
            ? new Color("d9d3c0")
            : new Color("e0b42f");
    }
}
