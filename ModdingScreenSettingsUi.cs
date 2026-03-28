using Godot;
using Godot.Collections;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using System.Collections;
using System.Reflection;

namespace BetterSaves;

internal static class ModdingScreenSettingsUi
{
    private const string SettingsRowName = "BetterSavesSettingsSyncModeRow";
    private const string InstallScheduledMetaName = "_better_saves_settings_install_scheduled";
    private const string NativePaginatorMetaName = "_better_saves_native_paginator";
    private const string NativeArrowMetaName = "_better_saves_native_arrow";
    private const string NativeArrowDirectionMetaName = "_better_saves_native_arrow_direction";
    private const string ValueDisplayMetaName = "_better_saves_value_display";
    private const string MainValueLabelName = "BetterSavesValueLabel";
    private const string VfxValueLabelName = "BetterSavesValueVfxLabel";
    private static readonly Vector2 ArrowSlotSize = new(82, 56);
    private static readonly Vector2 ValueSlotSize = new(190, 56);
    private const string NativePaginatorTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NPaginator";
    private static readonly Type? NativePaginatorType = AccessTools.TypeByName(NativePaginatorTypeName);
    private static readonly FieldInfo? PaginatorOptionsField = NativePaginatorType is null
        ? null
        : AccessTools.DeclaredField(NativePaginatorType, "_options") ?? AccessTools.Field(NativePaginatorType, "_options");
    private static readonly FieldInfo? PaginatorCurrentIndexField = NativePaginatorType is null
        ? null
        : AccessTools.DeclaredField(NativePaginatorType, "_currentIndex") ?? AccessTools.Field(NativePaginatorType, "_currentIndex");
    private static readonly FieldInfo? PaginatorLabelField = NativePaginatorType is null
        ? null
        : AccessTools.DeclaredField(NativePaginatorType, "_label") ?? AccessTools.Field(NativePaginatorType, "_label");
    private static readonly MethodInfo? PaginatorOnIndexChangedMethod = NativePaginatorType is null
        ? null
        : AccessTools.DeclaredMethod(NativePaginatorType, "OnIndexChanged", new[] { typeof(int) })
            ?? AccessTools.DeclaredMethod(NativePaginatorType, "OnIndexChanged");

    public static void InstallInSettingsScreen(Node? screen)
    {
        if (screen is not Control root)
        {
            return;
        }

        ScheduleInstall(root);
    }

    private static void ScheduleInstall(Control root)
    {
        if (root.HasMeta(InstallScheduledMetaName))
        {
            return;
        }

        root.SetMeta(InstallScheduledMetaName, true);

        var tree = root.GetTree();
        if (tree is null)
        {
            TryInstall(root);
            return;
        }

        var timer = tree.CreateTimer(0.0);
        timer.Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(root))
            {
                return;
            }

            root.SetMeta(InstallScheduledMetaName, false);
            TryInstall(root);
        };
    }

    private static void TryInstall(Control root)
    {
        var moddingAnchor = FindModdingAnchor(root);
        if (moddingAnchor is null)
        {
            Log.Info("[BetterSaves] Could not find the Modding settings row anchor.");
            return;
        }

        var anchorRow = FindSettingsRow(moddingAnchor);
        if (anchorRow is null || anchorRow.GetParent() is not Container parent)
        {
            Log.Info("[BetterSaves] Could not resolve native settings rows for sync mode injection.");
            return;
        }

        var templateRow = FindPaginatorTemplateRow(root, parent, anchorRow);
        var existingRow = FindExistingRow(root);
        if (existingRow is not null)
        {
            existingRow.GetParent()?.RemoveChild(existingRow);
            existingRow.QueueFree();
        }

        var clone = anchorRow.Duplicate() as Control;
        if (clone is null)
        {
            Log.Info("[BetterSaves] Failed to duplicate the anchor settings row.");
            return;
        }

        clone.Name = SettingsRowName;

        var installedAsNative = templateRow is not null && TryPrepareNativePaginatorRow(clone, templateRow, anchorRow);
        if (!installedAsNative)
        {
            PrepareFallbackRow(clone, anchorRow);
        }

        parent.AddChild(clone);
        parent.MoveChild(clone, anchorRow.GetIndex() + 1);

        Log.Info(
            $"[BetterSaves] Installed {(installedAsNative ? "native paginator" : "fallback")} sync mode row " +
            $"under '{anchorRow.Name}' using paginator template '{templateRow?.Name ?? "<none>"}'.");
    }

    private static bool TryPrepareNativePaginatorRow(Control row, Control templateRow, Control anchorRow)
    {
        var templatePaginator = FindPaginatorControl(templateRow);
        if (templatePaginator is null)
        {
            return false;
        }

        var templateTitleLabel = FindTitleTemplateLabel(templateRow, templatePaginator)
            ?? FindSettingsTitleLabel(anchorRow);

        var freshPaginator = CreateFreshPaginator(templatePaginator);
        if (freshPaginator is null)
        {
            Log.Info("[BetterSaves] Failed to create a fresh native paginator instance.");
            return false;
        }

        var visualArrowTemplates = FindArrowControls(freshPaginator)
            .Take(2)
            .ToList();
        if (visualArrowTemplates.Count != 2)
        {
            freshPaginator.Free();
            return false;
        }

        var visualValueTemplate = FindValueDisplayControl(freshPaginator, visualArrowTemplates);
        if (visualValueTemplate is null)
        {
            freshPaginator.Free();
            return false;
        }

        var customPaginator = CreateCustomNativePaginator(
            freshPaginator,
            visualArrowTemplates,
            visualValueTemplate);

        RemoveAllChildren(row);

        var layout = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };

        var title = CreateTitleLabel(templateTitleLabel);
        layout.AddChild(title);
        layout.AddChild(customPaginator);
        row.AddChild(layout);

        freshPaginator.Free();
        return true;
    }

    private static void PrepareFallbackRow(Control row, Control anchorRow)
    {
        var templateButton = FindFirstButtonLike(anchorRow);
        var templateLabel = FindSettingsTitleLabel(anchorRow);

        RemoveAllChildren(row);

        var layout = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };

        var title = CreateTitleLabel(templateLabel);
        var paginator = CreateFallbackPaginator(templateButton);

        layout.AddChild(title);
        layout.AddChild(paginator);
        row.AddChild(layout);
    }

    private static Label CreateTitleLabel(Label? template)
    {
        var label = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            Text = BetterSavesLocalization.GetPanelTitle()
        };

        if (template is not null)
        {
            CopyControlStyle(template, label);
            label.LabelSettings = template.LabelSettings;
            label.Modulate = template.Modulate;
            label.SelfModulate = template.SelfModulate;
        }

        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        label.Visible = true;
        return label;
    }

    private static Control CreateFallbackPaginator(Control? template)
    {
        var container = new HBoxContainer
        {
            SizeFlagsHorizontal = template?.SizeFlagsHorizontal ?? Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = template?.SizeFlagsVertical ?? Control.SizeFlags.ShrinkCenter,
            MouseFilter = template?.MouseFilter ?? Control.MouseFilterEnum.Pass
        };

        var leftButton = CreateFallbackPaginatorButton(template, "\u25c0");
        var valueButton = CreateFallbackValueLabel(template);
        var rightButton = CreateFallbackPaginatorButton(template, "\u25b6");

        RefreshValueDisplay(valueButton);
        ConnectPressed(leftButton, () =>
        {
            CycleBackward();
            RefreshValueDisplay(valueButton);
        });
        ConnectPressed(rightButton, () =>
        {
            CycleForward();
            RefreshValueDisplay(valueButton);
        });

        container.AddChild(leftButton);
        container.AddChild(valueButton);
        container.AddChild(rightButton);
        return container;
    }

    private static Button CreateFallbackPaginatorButton(Control? template, string text)
    {
        var button = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(68, template?.CustomMinimumSize.Y ?? 56),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = template?.SizeFlagsVertical ?? Control.SizeFlags.ShrinkCenter
        };

        if (template is not null)
        {
            CopyControlStyle(template, button);
        }

        button.SelfModulate = new Color("e0b42f");
        return button;
    }

    private static Control CreateFallbackValueLabel(Control? template)
    {
        var label = new Label
        {
            CustomMinimumSize = new Vector2(180, template?.CustomMinimumSize.Y ?? 56),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = template?.SizeFlagsVertical ?? Control.SizeFlags.ShrinkCenter,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (template is not null)
        {
            CopyControlStyle(template, label);
            label.Modulate = template.Modulate;
            label.SelfModulate = template.SelfModulate;
        }

        return label;
    }

    private static bool CanDisplayText(Control control)
    {
        return control is Label
            || control is Button
            || control.GetType().GetProperty("Text") is not null;
    }

    private static void SetDisplayedText(Control control, string text)
    {
        switch (control)
        {
            case Label label:
                label.Text = text;
                return;
            case Button button:
                button.Text = text;
                return;
        }

        var textProperty = control.GetType().GetProperty("Text");
        if (textProperty?.CanWrite == true && textProperty.PropertyType == typeof(string))
        {
            textProperty.SetValue(control, text);
        }
    }

    private static void ConnectPressed(Control control, Action action)
    {
        if (!control.HasSignal("pressed"))
        {
            return;
        }

        control.Connect("pressed", Callable.From(action));
    }

    private static void ClearPressedConnections(Control control)
    {
        if (!control.HasSignal("pressed"))
        {
            return;
        }

        foreach (Dictionary connection in control.GetSignalConnectionList("pressed"))
        {
            if (!connection.ContainsKey("callable"))
            {
                continue;
            }

            var callableVariant = (Variant)connection["callable"];
            if (callableVariant.VariantType != Variant.Type.Callable)
            {
                continue;
            }

            var callable = callableVariant.AsCallable();
            control.Disconnect("pressed", callable);
        }
    }

    private static void CycleBackward()
    {
        Log.Info("[BetterSaves] Sync mode left arrow pressed.");
        BetterSavesConfig.SetMode(BetterSavesConfig.CurrentMode == SyncMode.CurrentRunOnly
            ? SyncMode.FullSync
            : SyncMode.CurrentRunOnly);
    }

    private static void CycleForward()
    {
        Log.Info("[BetterSaves] Sync mode right arrow pressed.");
        BetterSavesConfig.SetMode(BetterSavesConfig.CurrentMode == SyncMode.CurrentRunOnly
            ? SyncMode.FullSync
            : SyncMode.CurrentRunOnly);
    }

    internal static void HandleNativePaginatorIndexChanged(object? instance)
    {
        if (instance is not Control paginator || !paginator.HasMeta(NativePaginatorMetaName))
        {
            return;
        }

        var mode = IndexToMode(ReadPaginatorIndex(paginator));
        BetterSavesConfig.SetMode(mode);
    }

    internal static bool HandleNativePaginateArrowRelease(object? instance)
    {
        if (instance is not Control arrow || !arrow.HasMeta(NativeArrowMetaName))
        {
            return false;
        }

        var directionVariant = arrow.GetMeta(NativeArrowDirectionMetaName);
        if (directionVariant.VariantType != Variant.Type.Int)
        {
            return false;
        }

        var direction = directionVariant.AsInt32();
        var valueDisplayVariant = arrow.GetMeta(ValueDisplayMetaName);
        if (valueDisplayVariant.VariantType != Variant.Type.Object)
        {
            return false;
        }

        if (valueDisplayVariant.AsGodotObject() is not Control valueDisplay)
        {
            return false;
        }

        if (direction < 0)
        {
            CycleBackward();
        }
        else
        {
            CycleForward();
        }

        RefreshValueDisplay(valueDisplay);
        return true;
    }

    private static void RefreshValueDisplay(Control valueDisplay)
    {
        var text = BetterSavesLocalization.GetModeDisplayName(BetterSavesConfig.CurrentMode);
        if (TryRefreshAnimatedValueDisplay(valueDisplay, text))
        {
            return;
        }

        switch (valueDisplay)
        {
            case Label label:
                label.Text = text;
                break;
            case Button button:
                button.Text = text;
                break;
        }
    }

    private static void CopyControlStyle(Control source, Control target)
    {
        target.Theme = source.Theme;
        target.ThemeTypeVariation = source.ThemeTypeVariation;
        target.MouseFilter = source.MouseFilter;
        target.SizeFlagsHorizontal = source.SizeFlagsHorizontal;
        target.SizeFlagsVertical = source.SizeFlagsVertical;
        target.CustomMinimumSize = source.CustomMinimumSize;
        target.Modulate = source.Modulate;
        target.SelfModulate = source.SelfModulate;
    }

    private static Control CreateCustomNativePaginator(
        Control templatePaginator,
        IReadOnlyList<Control> arrowTemplates,
        Control valueTemplate)
    {
        var container = new HBoxContainer
        {
            Name = templatePaginator.Name,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = templatePaginator.SizeFlagsVertical,
            CustomMinimumSize = templatePaginator.CustomMinimumSize,
            Theme = templatePaginator.Theme,
            ThemeTypeVariation = templatePaginator.ThemeTypeVariation,
            MouseFilter = Control.MouseFilterEnum.Pass,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        container.AddThemeConstantOverride("separation", 10);

        var valueDisplay = CreateNativeValueDisplay(valueTemplate);
        RefreshValueDisplay(valueDisplay);
        var leftArrow = CreateNativeArrowControl(arrowTemplates[0], -1, valueDisplay);
        var rightArrow = CreateNativeArrowControl(arrowTemplates[1], 1, valueDisplay);

        container.AddChild(leftArrow);
        container.AddChild(valueDisplay);
        container.AddChild(rightArrow);
        return container;
    }

    private static Control CreateNativeArrowControl(Control arrowTemplate, int direction, Control valueDisplay)
    {
        var arrow = arrowTemplate.Duplicate() as Control ?? throw new InvalidOperationException("Failed to duplicate native arrow.");
        arrow.SetMeta(NativeArrowMetaName, true);
        arrow.SetMeta(NativeArrowDirectionMetaName, direction);
        arrow.SetMeta(ValueDisplayMetaName, valueDisplay);
        arrow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        arrow.Position = Vector2.Zero;

        var slot = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = arrowTemplate.SizeFlagsVertical,
            CustomMinimumSize = new Vector2(
                Math.Max(ArrowSlotSize.X, arrowTemplate.CustomMinimumSize.X),
                Math.Max(ArrowSlotSize.Y, arrowTemplate.CustomMinimumSize.Y))
        };

        slot.AddChild(arrow);
        return slot;
    }

    private static Control CreateNativeValueDisplay(Control template)
    {
        var wrapper = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = template.SizeFlagsVertical,
            CustomMinimumSize = new Vector2(
                Math.Max(ValueSlotSize.X, template.CustomMinimumSize.X),
                Math.Max(ValueSlotSize.Y, template.CustomMinimumSize.Y))
        };

        var mainLabel = template.Duplicate() as Control ?? CreateFallbackValueLabel(template);
        mainLabel.Name = MainValueLabelName;
        mainLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        mainLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var vfxLabel = template.Duplicate() as Control ?? CreateFallbackValueLabel(template);
        vfxLabel.Name = VfxValueLabelName;
        vfxLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        vfxLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vfxLabel.Visible = false;

        wrapper.AddChild(vfxLabel);
        wrapper.AddChild(mainLabel);
        return wrapper;
    }

    private static bool ConfigureNativePaginator(Control paginator)
    {
        if (NativePaginatorType is null || !NativePaginatorType.IsInstanceOfType(paginator))
        {
            return false;
        }

        paginator.SetMeta(NativePaginatorMetaName, true);

        var options = GetModeDisplayNames();
        var index = ModeToIndex(BetterSavesConfig.CurrentMode);

        if (!WritePaginatorOptions(paginator, options))
        {
            return false;
        }

        WritePaginatorIndex(paginator, index);
        RefreshNativePaginator(paginator, options, index);
        return true;
    }

    private static string[] GetModeDisplayNames()
    {
        return
        [
            BetterSavesLocalization.GetModeDisplayName(SyncMode.CurrentRunOnly),
            BetterSavesLocalization.GetModeDisplayName(SyncMode.FullSync)
        ];
    }

    private static bool WritePaginatorOptions(Control paginator, IReadOnlyList<string> options)
    {
        if (PaginatorOptionsField is null)
        {
            return false;
        }

        var fieldType = PaginatorOptionsField.FieldType;
        object? value = null;

        if (fieldType == typeof(string[]))
        {
            value = options.ToArray();
        }
        else if (typeof(IList<string>).IsAssignableFrom(fieldType))
        {
            value = Activator.CreateInstance(fieldType);
            if (value is IList<string> stringList)
            {
                foreach (var option in options)
                {
                    stringList.Add(option);
                }
            }
        }
        else if (fieldType.IsGenericType
            && fieldType.GetGenericTypeDefinition() == typeof(Godot.Collections.Array<>)
            && fieldType.GenericTypeArguments[0] == typeof(string))
        {
            var array = new Godot.Collections.Array<string>();
            foreach (var option in options)
            {
                array.Add(option);
            }

            value = array;
        }
        else if (typeof(IList).IsAssignableFrom(fieldType))
        {
            value = Activator.CreateInstance(fieldType);
            if (value is IList list)
            {
                foreach (var option in options)
                {
                    list.Add(option);
                }
            }
        }

        if (value is null)
        {
            return false;
        }

        PaginatorOptionsField.SetValue(paginator, value);
        return true;
    }

    private static void WritePaginatorIndex(Control paginator, int index)
    {
        PaginatorCurrentIndexField?.SetValue(paginator, index);
    }

    private static int ReadPaginatorIndex(Control paginator)
    {
        if (PaginatorCurrentIndexField?.GetValue(paginator) is int index)
        {
            return index;
        }

        return 0;
    }

    private static void RefreshNativePaginator(Control paginator, IReadOnlyList<string> options, int index)
    {
        TryInvokePaginatorOnIndexChanged(paginator, index);

        if (PaginatorLabelField?.GetValue(paginator) is Control labelControl)
        {
            SetDisplayedText(labelControl, options[Math.Clamp(index, 0, options.Count - 1)]);
        }
    }

    private static bool TryInvokePaginatorOnIndexChanged(Control paginator, int index)
    {
        if (PaginatorOnIndexChangedMethod is null)
        {
            return false;
        }

        var parameters = PaginatorOnIndexChangedMethod.GetParameters();
        var args = parameters.Length switch
        {
            0 => System.Array.Empty<object>(),
            1 => new object[] { index },
            _ => null
        };

        if (args is null)
        {
            return false;
        }

        PaginatorOnIndexChangedMethod.Invoke(paginator, args);
        return true;
    }

    private static int ModeToIndex(SyncMode mode)
    {
        return mode == SyncMode.FullSync ? 1 : 0;
    }

    private static SyncMode IndexToMode(int index)
    {
        return index == 1 ? SyncMode.FullSync : SyncMode.CurrentRunOnly;
    }

    private static Control? CreateFreshPaginator(Control templatePaginator)
    {
        var scenePath = templatePaginator.SceneFilePath;
        if (string.IsNullOrWhiteSpace(scenePath))
        {
            Log.Info("[BetterSaves] Template paginator does not expose a SceneFilePath. Falling back to duplicating the live paginator as a visual template.");
            return templatePaginator.Duplicate() as Control;
        }

        var packedScene = ResourceLoader.Load<PackedScene>(scenePath);
        if (packedScene is null)
        {
            Log.Info($"[BetterSaves] Failed to load paginator scene '{scenePath}'.");
            return null;
        }

        var paginator = packedScene.Instantiate<Control>();
        paginator.Name = templatePaginator.Name;
        paginator.SizeFlagsHorizontal = templatePaginator.SizeFlagsHorizontal;
        paginator.SizeFlagsVertical = templatePaginator.SizeFlagsVertical;
        paginator.CustomMinimumSize = templatePaginator.CustomMinimumSize;
        paginator.Visible = templatePaginator.Visible;
        paginator.Theme = templatePaginator.Theme;
        paginator.ThemeTypeVariation = templatePaginator.ThemeTypeVariation;
        paginator.MouseFilter = templatePaginator.MouseFilter;
        return paginator;
    }

    private static void RemoveAllChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static Control? FindExistingRow(Control root)
    {
        return EnumerateDescendants(root)
            .OfType<Control>()
            .FirstOrDefault(control => string.Equals(control.Name.ToString(), SettingsRowName, StringComparison.Ordinal));
    }

    private static Control? FindPaginatorTemplateRow(Control root, Container parent, Control anchorRow)
    {
        var siblingRows = parent.GetChildren()
            .OfType<Control>()
            .Where(control => control != anchorRow)
            .OrderBy(control => Math.Abs(control.GetIndex() - anchorRow.GetIndex()))
            .ToList();

        var siblingTemplate = siblingRows.FirstOrDefault(ContainsPaginator);
        if (siblingTemplate is not null)
        {
            return siblingTemplate;
        }

        return EnumerateDescendants(root)
            .OfType<Control>()
            .Where(control => control != anchorRow)
            .FirstOrDefault(ContainsPaginator);
    }

    private static bool ContainsPaginator(Control row)
    {
        return FindPaginatorControl(row) is not null;
    }

    private static Control? FindPaginatorControl(Node root)
    {
        return EnumerateDescendants(root)
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.GetType().Name.Contains("Paginator", StringComparison.OrdinalIgnoreCase)
                || control.Name.ToString().Contains("Paginator", StringComparison.OrdinalIgnoreCase));
    }

    private static List<Control> FindArrowControls(Node root)
    {
        return EnumerateDescendants(root)
            .OfType<Control>()
            .Where(control =>
                control.GetType().Name.Contains("Arrow", StringComparison.OrdinalIgnoreCase)
                || control.Name.ToString().Contains("Arrow", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static Control? FindValueDisplayControl(Control paginator, IReadOnlyCollection<Control> arrows)
    {
        var label = EnumerateDescendants(paginator)
            .OfType<Label>()
            .FirstOrDefault(candidate => !arrows.Any(arrow => IsDescendantOf(candidate, arrow)));
        if (label is not null)
        {
            return label;
        }

        return EnumerateDescendants(paginator)
            .OfType<Button>()
            .FirstOrDefault(candidate => !arrows.Contains(candidate));
    }

    private static Control? FindModdingAnchor(Control root)
    {
        foreach (var child in EnumerateDescendants(root).OfType<Control>())
        {
            var name = child.Name.ToString();
            var typeName = child.GetType().Name;
            if (typeName.Contains("OpenModdingScreenButton", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OpenModdingScreenButton", StringComparison.OrdinalIgnoreCase)
                || name.Contains("OpenModding", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ModdingScreen", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ModdingButton", StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }

            if (child is Label label && label.Text.Contains("Modding", StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    private static Control? FindSettingsRow(Control start)
    {
        Node? current = start;
        Control? best = null;
        while (current is not null)
        {
            if (current is Control control && current.GetParent() is Container parent)
            {
                best = control;

                if (parent is VBoxContainer)
                {
                    return control;
                }
            }

            current = current.GetParent();
        }

        return best;
    }

    private static Label? FindFirstLabel(Node root)
    {
        return EnumerateDescendants(root).OfType<Label>().FirstOrDefault();
    }

    private static Label? FindSettingsTitleLabel(Node row)
    {
        var labels = EnumerateDescendants(row)
            .OfType<Label>()
            .Where(label =>
                !string.IsNullOrWhiteSpace(label.Text)
                && !label.Text.Contains(">", StringComparison.Ordinal)
                && !label.Text.Contains("<", StringComparison.Ordinal))
            .ToList();

        if (labels.Count == 0)
        {
            return null;
        }

        return labels
            .OrderByDescending(label => label.Text.Length)
            .ThenBy(label => GetNodeDepth(label))
            .First();
    }

    private static bool TryRefreshAnimatedValueDisplay(Control valueDisplay, string text)
    {
        var mainLabel = valueDisplay.GetNodeOrNull<Control>(MainValueLabelName);
        var vfxLabel = valueDisplay.GetNodeOrNull<Control>(VfxValueLabelName);
        if (mainLabel is null || vfxLabel is null)
        {
            return false;
        }

        var previousText = GetDisplayedText(mainLabel);
        SetDisplayedText(mainLabel, text);

        if (string.Equals(previousText, text, StringComparison.Ordinal))
        {
            return true;
        }

        SetDisplayedText(vfxLabel, previousText);
        vfxLabel.Visible = !string.IsNullOrEmpty(previousText);
        vfxLabel.Modulate = new Color(1f, 0.92f, 0.72f, 1f);
        vfxLabel.Position = Vector2.Zero;
        mainLabel.Scale = new Vector2(1.14f, 1.14f);
        mainLabel.Modulate = new Color(1f, 0.94f, 0.78f, 1f);

        var tween = valueDisplay.CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(mainLabel, "scale", Vector2.One, 0.18);
        tween.TweenProperty(mainLabel, "modulate", Colors.White, 0.18);
        if (!string.IsNullOrEmpty(previousText))
        {
            tween.TweenProperty(vfxLabel, "position:y", -16f, 0.20);
            tween.TweenProperty(vfxLabel, "modulate:a", 0f, 0.20);
        }

        tween.Finished += () =>
        {
            if (GodotObject.IsInstanceValid(vfxLabel))
            {
                vfxLabel.Visible = false;
                vfxLabel.Position = Vector2.Zero;
            }

            if (GodotObject.IsInstanceValid(mainLabel))
            {
                mainLabel.Scale = Vector2.One;
            }
        };

        return true;
    }

    private static string GetDisplayedText(Control control)
    {
        return control switch
        {
            Label label => label.Text,
            Button button => button.Text,
            _ => control.GetType().GetProperty("Text")?.GetValue(control) as string ?? string.Empty
        };
    }

    private static Control? FindFirstButtonLike(Node root)
    {
        return EnumerateDescendants(root)
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.GetType().Name.Contains("Button", StringComparison.OrdinalIgnoreCase)
                || control.Name.ToString().Contains("Button", StringComparison.OrdinalIgnoreCase)
                || control.Name.ToString().Contains("Dropdown", StringComparison.OrdinalIgnoreCase));
    }

    private static Label? FindTitleTemplateLabel(Node row, Node paginator)
    {
        return EnumerateDescendants(row)
            .OfType<Label>()
            .Where(label => !IsDescendantOf(label, paginator))
            .OrderByDescending(label => label.Text?.Length ?? 0)
            .FirstOrDefault();
    }

    private static int GetNodeDepth(Node node)
    {
        var depth = 0;
        var current = node;
        while (current.GetParent() is not null)
        {
            depth++;
            current = current.GetParent();
        }

        return depth;
    }

    private static bool IsDescendantOf(Node candidate, Node ancestor)
    {
        Node? current = candidate;
        while (current is not null)
        {
            if (current == ancestor)
            {
                return true;
            }

            current = current.GetParent();
        }

        return false;
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
}
