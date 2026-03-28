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
        return IsChinese() ? "BetterSaves\u4e92\u901a\u6a21\u5f0f" : "BetterSaves Interop Mode";
    }

    public static string GetModeDisplayName(SyncMode mode)
    {
        if (IsChinese())
        {
            return mode switch
            {
                SyncMode.CurrentRunOnly => "\u4ec5\u540c\u6b65\u5f53\u524d\u5c40",
                SyncMode.FullSync => "\u5b8c\u6574\u540c\u6b65",
                _ => mode.ToString()
            };
        }

        return mode switch
        {
            SyncMode.CurrentRunOnly => "Only Current Run",
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
                SyncMode.CurrentRunOnly => "\u53ea\u540c\u6b65\u6b63\u5728\u8fdb\u884c\u4e2d\u7684\u5b58\u6863\uff0c\u4e0d\u540c\u6b65\u89e3\u9501\u3001\u65f6\u95f4\u7ebf\u3001\u5386\u53f2\u8bb0\u5f55\u548c\u8bbe\u7f6e\u3002",
                SyncMode.FullSync => "\u540c\u6b65\u8be5\u5b58\u6863\u4f4d\u4e0b\u7684\u5168\u90e8\u6570\u636e\uff0c\u5305\u62ec\u8fdb\u5ea6\u3001\u5386\u53f2\u8bb0\u5f55\u548c\u56de\u653e\u6587\u4ef6\u3002",
                _ => string.Empty
            };
        }

        return mode switch
        {
            SyncMode.CurrentRunOnly =>
                "Syncs only the in-progress run save and leaves unlocks, timeline, history, and settings alone.",
            SyncMode.FullSync =>
                "Syncs all profile-scoped save data, including progression, history, and replay files.",
            _ => string.Empty
        };
    }

    public static string GetActiveSaveTypeBadgeText()
    {
        var isVanilla = VanillaModeCompatibilityPatches.IsCompatibilityModeEnabled;
        if (IsChinese())
        {
            return isVanilla ? "\u539f\u7248\u5b58\u6863" : "\u6a21\u7ec4\u5b58\u6863";
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
