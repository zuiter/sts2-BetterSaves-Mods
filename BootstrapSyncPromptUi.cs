using Godot;
using HarmonyLib;

namespace BetterSaves;

internal static class BootstrapSyncPromptUi
{
    private const string OverlayName = "BetterSavesBootstrapPromptOverlay";
    private const string NoticeName = "BetterSavesBootstrapNotice";
    private const string ProfileScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen.NProfileScreen";
    private static bool _checkScheduled;
    private static bool _promptVisible;

    public static void InstallInMainMenu(Node? node)
    {
        if (node is not Control root)
        {
            return;
        }

        SchedulePromptCheck(root);
    }

    public static void InstallInProfileButton(Node? node)
    {
        if (node is not Control root)
        {
            return;
        }

        SchedulePromptCheck(root);
    }

    private static void SchedulePromptCheck(Control root)
    {
        if (_checkScheduled)
        {
            return;
        }

        var tree = root.GetTree();
        if (tree is null)
        {
            return;
        }

        _checkScheduled = true;
        var timer = tree.CreateTimer(0.25);
        timer.Timeout += () =>
        {
            _checkScheduled = false;

            if (!GodotObject.IsInstanceValid(root) || _promptVisible)
            {
                return;
            }

            if (TryShowPrompt(root))
            {
                return;
            }

            if (BetterSavesConfig.IsSyncEnabled && BetterSavesConfig.IsBootstrapPending)
            {
                SchedulePromptCheck(root);
            }
        };
    }

    private static bool TryShowPrompt(Control root)
    {
        if (!SaveInteropService.TryGetPendingBootstrapPrompt(out var prompt))
        {
            return false;
        }

        var tree = root.GetTree();
        var host = tree?.CurrentScene ?? tree?.Root;
        if (host is null)
        {
            return false;
        }

        if (host.GetNodeOrNull<Control>(OverlayName) is not null)
        {
            _promptVisible = true;
            return true;
        }

        var overlay = BuildOverlay(root, prompt);
        host.AddChild(overlay);
        _promptVisible = true;
        return true;
    }

    private static Control BuildOverlay(Control templateRoot, SaveInteropService.BootstrapPromptRequest prompt)
    {
        var overlay = new Control
        {
            Name = OverlayName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All
        };
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var dim = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(dim);

        var center = new CenterContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(720f, 0f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        center.AddChild(panel);

        var content = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        content.AddThemeConstantOverride("separation", 18);
        panel.AddChild(content);

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.RemoveChild(content);
        margin.AddChild(content);
        panel.AddChild(margin);

        var title = new Label
        {
            Text = GetPromptTitle(),
            HorizontalAlignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };

        var body = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = false,
            SelectionEnabled = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = GetPromptBody(prompt)
        };

        if (templateRoot.Theme is not null)
        {
            title.Theme = templateRoot.Theme;
            body.Theme = templateRoot.Theme;
            panel.Theme = templateRoot.Theme;
            margin.Theme = templateRoot.Theme;
            content.Theme = templateRoot.Theme;
        }

        content.AddChild(title);
        content.AddChild(body);

        var buttons = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        buttons.AddThemeConstantOverride("separation", 12);

        var skipButton = new Button
        {
            Text = GetSkipText()
        };
        skipButton.Pressed += () =>
        {
            SaveInteropService.DeclinePendingBootstrapPrompt("ui skip bootstrap import");
            CloseOverlay(overlay);
        };

        var useVanillaButton = new Button
        {
            Text = GetUseVanillaText()
        };
        useVanillaButton.Pressed += () =>
        {
            var imported = SaveInteropService.ConfirmPendingBootstrapPrompt(
                "ui choose vanilla authority",
                SaveInteropService.BootstrapImportAction.VanillaToModded);
            CloseOverlay(overlay);
            if (imported)
            {
                ShowImportNotice(overlay, prompt, SaveInteropService.BootstrapImportAction.VanillaToModded);
                RefreshBetterSavesUi(overlay, prompt);
            }
        };

        var useModdedButton = new Button
        {
            Text = GetUseModdedText()
        };
        useModdedButton.Pressed += () =>
        {
            var imported = SaveInteropService.ConfirmPendingBootstrapPrompt(
                "ui choose modded authority",
                SaveInteropService.BootstrapImportAction.ModdedToVanilla);
            CloseOverlay(overlay);
            if (imported)
            {
                ShowImportNotice(overlay, prompt, SaveInteropService.BootstrapImportAction.ModdedToVanilla);
                RefreshBetterSavesUi(overlay, prompt);
            }
        };

        buttons.AddChild(skipButton);
        buttons.AddChild(useVanillaButton);
        buttons.AddChild(useModdedButton);
        var focusButton = useVanillaButton;

        content.AddChild(buttons);

        overlay.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventKey keyEvent
                && keyEvent.Pressed
                && !keyEvent.Echo
                && keyEvent.Keycode == Key.Escape)
            {
                SaveInteropService.DismissPendingBootstrapPrompt("ui escape");
                CloseOverlay(overlay);
            }
        };

        var focusTimer = templateRoot.GetTree()?.CreateTimer(0.0);
        if (focusTimer is not null)
        {
            focusTimer.Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(focusButton) && focusButton.IsInsideTree())
                {
                    focusButton.GrabFocus();
                }
            };
        }

        return overlay;
    }

    private static void CloseOverlay(Control overlay)
    {
        _promptVisible = false;
        if (GodotObject.IsInstanceValid(overlay))
        {
            overlay.QueueFree();
        }
    }

    private static void RefreshBetterSavesUi(Control overlay, SaveInteropService.BootstrapPromptRequest prompt)
    {
        var tree = overlay.GetTree();
        if (tree is null)
        {
            return;
        }

        ScheduleLightUiRefresh(tree);
    }

    private static void ShowImportNotice(
        Control overlay,
        SaveInteropService.BootstrapPromptRequest prompt,
        SaveInteropService.BootstrapImportAction action)
    {
        var tree = overlay.GetTree();
        if (tree is null)
        {
            return;
        }

        var host = tree?.CurrentScene ?? tree?.Root;
        if (host is not Control root)
        {
            return;
        }

        if (root.GetNodeOrNull<Control>(NoticeName) is Control existing)
        {
            existing.QueueFree();
        }

        var notice = new PanelContainer
        {
            Name = NoticeName,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        notice.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        notice.OffsetLeft = 24;
        notice.OffsetTop = 24;
        notice.OffsetRight = -24;
        notice.OffsetBottom = 104;

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        notice.AddChild(margin);

        var label = new Label
        {
            Text = GetImportNoticeText(prompt, action),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        margin.AddChild(label);

        root.AddChild(notice);
        root.MoveChild(notice, root.GetChildCount() - 1);

        var timer = tree!.CreateTimer(3.0);
        if (timer is null)
        {
            return;
        }

        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(notice))
            {
                notice.QueueFree();
            }
        };
    }

    private static void TryReloadCurrentProfileData()
    {
        try
        {
            var saveManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.SaveManager");
            if (saveManagerType is null)
            {
                return;
            }

            var saveManager = AccessTools.Property(saveManagerType, "Instance")?.GetValue(null);
            if (saveManager is null)
            {
                return;
            }

            var currentProfileIdObject = AccessTools.Property(saveManagerType, "CurrentProfileId")?.GetValue(saveManager);
            if (currentProfileIdObject is not int currentProfileId || currentProfileId is < 1 or > 3)
            {
                return;
            }

            var switchProfileId = AccessTools.Method(saveManagerType, "SwitchProfileId");
            if (switchProfileId is not null)
            {
                switchProfileId.Invoke(saveManager, new object?[] { currentProfileId });
                return;
            }

            var initProfileId = AccessTools.Method(saveManagerType, "InitProfileId");
            initProfileId?.Invoke(saveManager, new object?[] { currentProfileId });
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[BetterSaves] Failed to reload current profile data after bootstrap import: {ex.Message}");
        }
    }

    private static bool TryRefreshProfileScreen(Node root, string reason)
    {
        try
        {
            var refreshed = false;
            foreach (var node in EnumerateSelfAndDescendants(root))
            {
                if (node.GetType().FullName != ProfileScreenTypeName)
                {
                    continue;
                }

                ScheduleProfileScreenRefresh(node, reason);
                refreshed = true;
            }

            return refreshed;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[BetterSaves] Failed to refresh profile screen after bootstrap import: {ex.Message}");
            return false;
        }
    }

    private static void ScheduleProfileScreenRefresh(Node profileScreen, string reason)
    {
        var tree = profileScreen.GetTree();
        if (tree is null)
        {
            return;
        }

        var delays = new[] { 0.0, 0.15, 0.4 };
        foreach (var delay in delays)
        {
            var timer = tree.CreateTimer(delay);
            timer.Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(profileScreen))
                {
                    return;
                }

                profileScreen.CallDeferred("Refresh");
                RefreshBetterSavesBadges(profileScreen);
                GD.Print($"[BetterSaves] Refreshed profile screen after bootstrap import ({reason}).");
            };
        }

    }

    private static void ScheduleLightUiRefresh(SceneTree tree)
    {
        if (!GodotObject.IsInstanceValid(tree))
        {
            return;
        }

        var delays = new[] { 0.05, 0.20, 0.45 };
        for (var passIndex = 0; passIndex < delays.Length; passIndex++)
        {
            var delay = delays[passIndex];
            var currentPass = passIndex + 1;
            var timer = tree.CreateTimer(delay);
            timer.Timeout += () =>
            {
                if (!GodotObject.IsInstanceValid(tree))
                {
                    return;
                }

                TryReloadCurrentProfileData();

                var root = tree.CurrentScene ?? tree.Root;
                if (!GodotObject.IsInstanceValid(root))
                {
                    return;
                }

                if (currentPass == 1)
                {
                    ShowRefreshFlash(root);
                }

                TryRefreshProfileScreen(root, $"light refresh pass {currentPass}");
                var invokedCount = TryInvokeCurrentPageRefreshMethods(root);
                RefreshBetterSavesBadges(root);
                InstallPromptCheckForCurrentScene(root);
                GD.Print(
                    $"[BetterSaves] Soft-refreshed current page after bootstrap import " +
                    $"(pass {currentPass}, invoked {invokedCount} methods).");
            };
        }
    }

    private static int TryInvokeCurrentPageRefreshMethods(Node root)
    {
        var invokedCount = 0;
        foreach (var node in EnumerateSelfAndDescendants(root))
        {
            var typeName = node.GetType().FullName ?? string.Empty;
            if (!IsLikelyRefreshTarget(typeName))
            {
                continue;
            }

            invokedCount += TryInvokeRefreshMethods(
                node,
                [
                    "Refresh",
                    "RefreshInfo",
                    "RefreshState",
                    "RefreshVisibility",
                    "UpdateContinueRunInfo",
                    "UpdateContinueButton",
                    "UpdateInfo",
                    "UpdateState",
                    "UpdateText",
                    "RefreshButtons",
                    "RefreshButton",
                    "UpdateButtons",
                    "UpdateButton"
                ]);
        }

        return invokedCount;
    }

    private static bool IsLikelyRefreshTarget(string typeName)
    {
        return typeName.Contains("MainMenu", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Continue", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("RunInfo", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Profile", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Save", StringComparison.OrdinalIgnoreCase);
    }

    private static int TryInvokeRefreshMethods(Node node, IReadOnlyList<string> methodNames)
    {
        var invokedCount = 0;

        try
        {
            var type = node.GetType();
            foreach (var methodName in methodNames)
            {
                var method = AccessTools.Method(type, methodName, Type.EmptyTypes);
                if (method is null)
                {
                    continue;
                }

                method.Invoke(node, []);
                invokedCount++;
                GD.Print($"[BetterSaves] Invoked '{type.FullName}.{method.Name}' while soft-refreshing after bootstrap import.");
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[BetterSaves] Failed to invoke page refresh methods on '{node.GetType().FullName}': {ex.Message}");
        }

        return invokedCount;
    }

    private static void ShowRefreshFlash(Node root)
    {
        if (root is not Control controlRoot)
        {
            return;
        }

        if (controlRoot.GetNodeOrNull<Control>("BetterSavesRefreshFlash") is Control existing)
        {
            existing.QueueFree();
        }

        var flash = new ColorRect
        {
            Name = "BetterSavesRefreshFlash",
            Color = new Color(0f, 0f, 0f, 0.16f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        flash.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        controlRoot.AddChild(flash);
        controlRoot.MoveChild(flash, controlRoot.GetChildCount() - 1);

        var tree = controlRoot.GetTree();
        if (tree is null)
        {
            return;
        }

        var timer = tree.CreateTimer(0.16);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(flash))
            {
                flash.QueueFree();
            }
        };
    }

    private static void RefreshBetterSavesBadges(Node root)
    {
        foreach (var node in EnumerateDescendants(root))
        {
            if (node is Control control && control.Name == "BetterSavesSaveTypeBadge")
            {
                if (control.GetParent() is Control parent)
                {
                    ProfileScreenSaveTypeUi.InstallInProfileButton(parent);
                }
            }
        }
    }

    private static void InstallPromptCheckForCurrentScene(Node root)
    {
        if (root is Control control)
        {
            SchedulePromptCheck(control);
        }

        foreach (var node in EnumerateDescendants(root))
        {
            if (node is Control descendantControl)
            {
                SchedulePromptCheck(descendantControl);
            }
        }
    }

    private static IEnumerable<Node> EnumerateDescendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;

            foreach (var descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<Node> EnumerateSelfAndDescendants(Node node)
    {
        yield return node;

        foreach (var descendant in EnumerateDescendants(node))
        {
            yield return descendant;
        }
    }

    private static string GetPromptTitle()
    {
        return BetterSavesLocalization.IsChinese()
            ? "\u9996\u6b21\u540c\u6b65\u786e\u8ba4"
            : "BetterSaves First Sync";
    }

    private static string GetPromptBody(SaveInteropService.BootstrapPromptRequest prompt)
    {
        return BetterSavesLocalization.IsChinese()
            ? $"\u8fd9\u662f BetterSaves \u9996\u6b21\u63a5\u7ba1 profile{prompt.ProfileIndex} \u7684\u5b58\u6863\u3002\n\n\u8bf7\u9009\u62e9\u8fd9\u6b21\u9996\u6b21\u5bfc\u5165\u7684\u65b9\u5411\uff1a\n- \u4ee5\u539f\u7248\u4e3a\u51c6\uff1a\u628a\u539f\u7248\u5b58\u6863\u540c\u6b65\u5230\u6a21\u7ec4\u5b58\u6863\n- \u4ee5\u6a21\u7ec4\u4e3a\u51c6\uff1a\u628a\u6a21\u7ec4\u5b58\u6863\u540c\u6b65\u5230\u539f\u7248\u5b58\u6863\n- \u6682\u4e0d\u540c\u6b65\uff1a\u8df3\u8fc7\u672c\u6b21\u9996\u6b21\u5bfc\u5165\n\n\u9009\u62e9\u4e4b\u540e BetterSaves \u4f1a\u7acb\u5373\u5bf9 profile{prompt.ProfileIndex} \u6267\u884c\uff08\u6216\u8df3\u8fc7\uff09\u8fd9\u6b21\u9996\u6b21\u5bfc\u5165\uff0c\u4e4b\u540e\u4e0d\u518d\u91cd\u590d\u8be2\u95ee\u3002"
            : $"This is the first time BetterSaves is managing profile {prompt.ProfileIndex}.\n\nChoose how the one-time bootstrap import should work:\n- Use Vanilla: import vanilla save data into the modded save\n- Use Modded: import modded save data into the vanilla save\n- Not Now: skip this initial bootstrap import\n\nAfter you choose, BetterSaves will immediately perform (or skip) this one-time import for profile {prompt.ProfileIndex} and will not ask again.";
    }

    private static string GetConfirmText()
    {
        return BetterSavesLocalization.IsChinese()
            ? "\u7acb\u5373\u5bfc\u5165"
            : "Import Now";
    }

    private static string GetDeclineText()
    {
        return BetterSavesLocalization.IsChinese()
            ? "\u6682\u4e0d\u5bfc\u5165"
            : "Not Now";
    }

    private static string GetSkipText()
    {
        return BetterSavesLocalization.IsChinese()
            ? "\u6682\u4e0d\u51b3\u5b9a"
            : "Not Now";
    }

    private static string GetUseVanillaText()
    {
        return BetterSavesLocalization.IsChinese()
            ? "\u4ee5\u539f\u7248\u4e3a\u51c6"
            : "Use Vanilla";
    }

    private static string GetUseModdedText()
    {
        return BetterSavesLocalization.IsChinese()
            ? "\u4ee5\u6a21\u7ec4\u4e3a\u51c6"
            : "Use Modded";
    }

    private static string GetImportNoticeText(
        SaveInteropService.BootstrapPromptRequest prompt,
        SaveInteropService.BootstrapImportAction action)
    {
        if (BetterSavesLocalization.IsChinese())
        {
            return action switch
            {
                SaveInteropService.BootstrapImportAction.VanillaToModded =>
                    $"\u5df2\u5b8c\u6210 profile{prompt.ProfileIndex} \u7684\u9996\u6b21\u5bfc\u5165\uff1a\u539f\u7248\u5b58\u6863 -> \u6a21\u7ec4\u5b58\u6863\u3002",
                SaveInteropService.BootstrapImportAction.ModdedToVanilla =>
                    $"\u5df2\u5b8c\u6210 profile{prompt.ProfileIndex} \u7684\u9996\u6b21\u5bfc\u5165\uff1a\u6a21\u7ec4\u5b58\u6863 -> \u539f\u7248\u5b58\u6863\u3002",
                _ => "\u5df2\u5b8c\u6210 BetterSaves \u9996\u6b21\u5bfc\u5165\u3002"
            };
        }

        return action switch
        {
            SaveInteropService.BootstrapImportAction.VanillaToModded =>
                $"BetterSaves completed the first import for profile {prompt.ProfileIndex}: vanilla -> modded.",
            SaveInteropService.BootstrapImportAction.ModdedToVanilla =>
                $"BetterSaves completed the first import for profile {prompt.ProfileIndex}: modded -> vanilla.",
            _ => "BetterSaves completed the first import."
        };
    }
}
