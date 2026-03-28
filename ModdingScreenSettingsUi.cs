using Godot;
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
    private const string NativePaginatorIndexMetaName = "_better_saves_native_paginator_index";
    private const string ValueDisplayBaseScaleMetaName = "_better_saves_value_display_base_scale";
    private const string ValueDisplayBaseModulateMetaName = "_better_saves_value_display_base_modulate";
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
    private static readonly FieldInfo? PaginatorVfxLabelField = NativePaginatorType is null
        ? null
        : AccessTools.DeclaredField(NativePaginatorType, "_vfxLabel")
            ?? AccessTools.Field(NativePaginatorType, "_vfxLabel")
            ?? AccessTools.DeclaredField(NativePaginatorType, "VfxLabel")
            ?? AccessTools.Field(NativePaginatorType, "VfxLabel");

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
        var visualTemplateRow = templateRow;
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

        var installedAsNative = templateRow is not null && TryPrepareNativePaginatorRow(clone, templateRow, visualTemplateRow, anchorRow);
        if (!installedAsNative)
        {
            PrepareFallbackRow(clone, anchorRow);
        }

        parent.AddChild(clone);
        parent.MoveChild(clone, anchorRow.GetIndex() + 1);

        if (installedAsNative && FindPaginatorControl(clone) is Control installedPaginator)
        {
            ScheduleNativePaginatorRefresh(installedPaginator);
        }

        Log.Info(
            $"[BetterSaves] Installed {(installedAsNative ? "native paginator" : "fallback")} sync mode row " +
            $"under '{anchorRow.Name}' using paginator template '{templateRow?.Name ?? "<none>"}'.");
    }

    private static bool TryPrepareNativePaginatorRow(Control row, Control templateRow, Control? visualTemplateRow, Control anchorRow)
    {
        var templatePaginator = FindPaginatorControl(templateRow);
        if (templatePaginator is null)
        {
            return false;
        }

        var visualTemplatePaginator = visualTemplateRow is null ? null : FindPaginatorControl(visualTemplateRow);

        var templateTitleLabel = FindSettingsTitleLabel(templateRow, templatePaginator)
            ?? FindSettingsTitleLabel(anchorRow, null);

        var freshPaginator = CreateFreshPaginator(templatePaginator);
        if (freshPaginator is null)
        {
            Log.Info("[BetterSaves] Failed to create a fresh native paginator instance.");
            return false;
        }

        MakeVisualResourcesUnique(row);
        MakeVisualResourcesUnique(freshPaginator);
        if (visualTemplatePaginator is not null)
        {
            ApplyPaginatorVisualTemplate(freshPaginator, visualTemplatePaginator);
        }
        RemoveHoverTipNodes(row);
        ClearHoverTipBindings(row);
        RemoveAllChildren(row);

        var layout = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };

        var title = CreateTitleLabel(templateTitleLabel);
        UpdateNativeTextBindings(title, BetterSavesLocalization.GetPanelTitle(), BetterSavesLocalization.GetPanelDescription());

        if (!ConfigureNativePaginator(freshPaginator))
        {
            freshPaginator.Free();
            return false;
        }

        BindNativePaginatorControls(freshPaginator);
        freshPaginator.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        freshPaginator.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        freshPaginator.MouseFilter = Control.MouseFilterEnum.Pass;

        layout.AddChild(title);
        layout.AddChild(freshPaginator);
        row.AddChild(layout);
        return true;
    }

    private static void PrepareFallbackRow(Control row, Control anchorRow)
    {
        var templateButton = FindFirstButtonLike(anchorRow);
        var templateTitleLabel = FindSettingsTitleLabel(anchorRow, null);

        RemoveAllChildren(row);

        var layout = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };

        var title = CreateTitleLabel(templateTitleLabel);
        var paginator = CreateFallbackPaginator(templateButton);

        layout.AddChild(title);
        layout.AddChild(paginator);
        row.AddChild(layout);
    }

    private static Label CreateTitleLabel(Label? template)
    {
        var label = template?.Duplicate() as Label
            ?? new Label
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Text = BetterSavesLocalization.GetPanelTitle(),
                Visible = true
            };

        MakeVisualResourcesUnique(label);
        label.Name = "BetterSavesTitleLabel";
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.Text = BetterSavesLocalization.GetPanelTitle();
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

    internal static bool HandleNativePaginatorIndexChanged(object? instance, int index)
    {
        if (instance is not Control paginator || !paginator.HasMeta(NativePaginatorMetaName))
        {
            return false;
        }

        WritePaginatorIndex(paginator, index);
        var mode = IndexToMode(index);
        BetterSavesConfig.SetMode(mode);
        RefreshNativePaginator(paginator, GetModeDisplayNames(), index);
        return true;
    }

    internal static bool HandleNativePaginatorReady(object? instance)
    {
        if (instance is not Control paginator || !paginator.HasMeta(NativePaginatorMetaName))
        {
            return false;
        }

        var arrows = FindArrowControls(paginator);
        var labels = EnumerateDescendants(paginator)
            .OfType<Label>()
            .Where(label => !arrows.Any(arrow => IsDescendantOf(label, arrow)))
            .OrderBy(GetControlSortX)
            .ToList();

        if (labels.Count > 0)
        {
            PaginatorLabelField?.SetValue(paginator, labels[0]);
        }

        if (labels.Count > 1 && PaginatorVfxLabelField is not null)
        {
            try
            {
                PaginatorVfxLabelField.SetValue(paginator, labels[1]);
            }
            catch
            {
                // Some paginator variants do not expose a writable VFX label field.
            }
        }

        RefreshNativePaginator(paginator, GetModeDisplayNames(), ReadPaginatorIndex(paginator));
        return true;
    }

    internal static bool HandleNativePaginatorIndexChangeHelper(object? instance, bool pagedLeft)
    {
        if (instance is not Control paginator || !paginator.HasMeta(NativePaginatorMetaName))
        {
            return false;
        }

        var options = GetModeDisplayNames();
        if (options.Length == 0)
        {
            return true;
        }

        var currentIndex = Math.Clamp(ReadPaginatorIndex(paginator), 0, options.Length - 1);
        var nextIndex = pagedLeft
            ? (currentIndex + options.Length - 1) % options.Length
            : (currentIndex + 1) % options.Length;

        WritePaginatorIndex(paginator, nextIndex);
        BetterSavesConfig.SetMode(IndexToMode(nextIndex));
        RefreshNativePaginator(paginator, options, nextIndex);
        return true;
    }

    private static void RefreshValueDisplay(Control valueDisplay)
    {
        var text = BetterSavesLocalization.GetModeDisplayName(BetterSavesConfig.CurrentMode);
        var previousText = GetDisplayedText(valueDisplay);
        switch (valueDisplay)
        {
            case Label label:
                label.Text = text;
                break;
            case Button button:
                button.Text = text;
                break;
            default:
            {
                var textProperty = valueDisplay.GetType().GetProperty("Text");
                if (textProperty?.CanWrite == true && textProperty.PropertyType == typeof(string))
                {
                    textProperty.SetValue(valueDisplay, text);
                }

                break;
            }
        }

        if (!string.Equals(previousText, text, StringComparison.Ordinal))
        {
            AnimateValueDisplay(valueDisplay);
        }
    }

    private static void ApplyNativeRowTitle(Control row, Control paginator)
    {
        var titleControls = EnumerateDescendants(row)
            .OfType<Control>()
            .Where(control => control != paginator)
            .Where(control => !IsDescendantOf(control, paginator))
            .Where(control => CanDisplayText(control))
            .Where(control => !HasHoverTipAncestor(control))
            .OrderBy(control => GetControlSortX(control))
            .ToList();

        if (titleControls.Count == 0)
        {
            var fallbackLabel = FindSettingsTitleLabel(row, paginator);
            if (fallbackLabel is not null)
            {
                fallbackLabel.Text = BetterSavesLocalization.GetPanelTitle();
                fallbackLabel.Visible = true;
            }

            return;
        }

        foreach (var control in titleControls)
        {
            SetDisplayedText(control, BetterSavesLocalization.GetPanelTitle());
            UpdateNativeTextBindings(control, BetterSavesLocalization.GetPanelTitle(), BetterSavesLocalization.GetPanelDescription());
            control.Visible = true;
        }
    }

    private static void BindNativePaginatorControls(Control paginator)
    {
        var arrows = FindArrowControls(paginator)
            .OrderBy(GetControlSortX)
            .ToList();

        var valueDisplay = FindValueDisplayControl(paginator, arrows);
        if (valueDisplay is not null)
        {
            RefreshValueDisplay(valueDisplay);
        }

        foreach (var arrow in arrows)
        {
            ClearHoverTipBindings(arrow);
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
            var fieldTypeName = PaginatorOptionsField?.FieldType.FullName ?? "<missing>";
            Log.Info($"[BetterSaves] Could not write paginator options using field type '{fieldTypeName}'. Continuing with BetterSaves-managed labels.");
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
        paginator.SetMeta(NativePaginatorIndexMetaName, index);

        if (PaginatorCurrentIndexField is null)
        {
            return;
        }

        try
        {
            PaginatorCurrentIndexField.SetValue(paginator, index);
        }
        catch
        {
            // The fallback meta still keeps BetterSaves pagination stable.
        }
    }

    private static int ReadPaginatorIndex(Control paginator)
    {
        if (paginator.HasMeta(NativePaginatorIndexMetaName))
        {
            return ((Variant)paginator.GetMeta(NativePaginatorIndexMetaName)).AsInt32();
        }

        if (PaginatorCurrentIndexField is not null)
        {
            try
            {
                if (PaginatorCurrentIndexField.GetValue(paginator) is int fieldIndex)
                {
                    return fieldIndex;
                }
            }
            catch
            {
                // Fall back to BetterSaves-managed metadata below.
            }
        }

        return 0;
    }

    private static void RefreshNativePaginator(Control paginator, IReadOnlyList<string> options, int index)
    {
        var text = options[Math.Clamp(index, 0, options.Count - 1)];

        if (PaginatorLabelField?.GetValue(paginator) is Control labelControl)
        {
            SetDisplayedText(labelControl, text);
        }

        if (PaginatorVfxLabelField?.GetValue(paginator) is Control vfxLabelControl)
        {
            SetDisplayedText(vfxLabelControl, text);
        }

        RefreshAllPaginatorTextLayers(paginator, text);
    }

    private static void RefreshAllPaginatorTextLayers(Control paginator, string text)
    {
        var arrows = FindArrowControls(paginator);
        var textControls = EnumerateDescendants(paginator)
            .OfType<Control>()
            .Where(control => CanDisplayText(control))
            .Where(control => !arrows.Any(arrow => IsDescendantOf(control, arrow)))
            .ToList();

        foreach (var control in textControls)
        {
            SetDisplayedText(control, text);
        }
    }

    private static void ScheduleNativePaginatorRefresh(Control paginator)
    {
        var tree = paginator.GetTree();
        if (tree is null)
        {
            return;
        }

        var timer = tree.CreateTimer(0.0);
        timer.Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(paginator))
            {
                return;
            }

            var index = ModeToIndex(BetterSavesConfig.CurrentMode);
            WritePaginatorIndex(paginator, index);
            RefreshNativePaginator(paginator, GetModeDisplayNames(), index);
        };
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

        var instantiatedRoot = packedScene.Instantiate<Control>();
        var paginator = instantiatedRoot;
        if (NativePaginatorType is not null && !NativePaginatorType.IsInstanceOfType(paginator))
        {
            var nestedPaginator = FindPaginatorControl(instantiatedRoot);
            if (nestedPaginator is not null && NativePaginatorType.IsInstanceOfType(nestedPaginator))
            {
                nestedPaginator.GetParent()?.RemoveChild(nestedPaginator);
                instantiatedRoot.QueueFree();
                paginator = nestedPaginator;
            }
            else
            {
                Log.Info(
                    $"[BetterSaves] Scene '{scenePath}' instantiated '{instantiatedRoot.GetType().FullName}', " +
                    "which does not expose an NPaginator root or child. Falling back to duplicating the live paginator.");
                instantiatedRoot.QueueFree();
                return templatePaginator.Duplicate() as Control;
            }
        }

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

        var siblingTemplate = siblingRows.FirstOrDefault(HasInstantiablePaginatorTemplate)
            ?? siblingRows.FirstOrDefault(ContainsPaginator);
        if (siblingTemplate is not null)
        {
            return siblingTemplate;
        }

        return EnumerateDescendants(root)
            .OfType<Control>()
            .Where(control => control != anchorRow)
            .FirstOrDefault(HasInstantiablePaginatorTemplate)
            ?? EnumerateDescendants(root)
                .OfType<Control>()
                .Where(control => control != anchorRow)
                .FirstOrDefault(ContainsPaginator);
    }

    private static bool ContainsPaginator(Control row)
    {
        return FindPaginatorControl(row) is not null;
    }

    private static bool HasInstantiablePaginatorTemplate(Control row)
    {
        var paginator = FindPaginatorControl(row);
        return paginator is not null && !string.IsNullOrWhiteSpace(paginator.SceneFilePath);
    }

    private static void ApplyPaginatorVisualTemplate(Control targetPaginator, Control sourcePaginator)
    {
        CopyControlStyle(sourcePaginator, targetPaginator);
        targetPaginator.CustomMinimumSize = sourcePaginator.CustomMinimumSize;

        var sourceArrows = FindArrowControls(sourcePaginator)
            .OrderBy(GetControlSortX)
            .ToList();
        var targetArrows = FindArrowControls(targetPaginator)
            .OrderBy(GetControlSortX)
            .ToList();

        for (var i = 0; i < Math.Min(sourceArrows.Count, targetArrows.Count); i++)
        {
            CopyVisualStyle(sourceArrows[i], targetArrows[i]);
        }

        var sourceValue = FindValueDisplayControl(sourcePaginator, sourceArrows);
        var targetValue = FindValueDisplayControl(targetPaginator, targetArrows);
        if (sourceValue is not null && targetValue is not null)
        {
            CopyVisualStyle(sourceValue, targetValue);
        }
    }

    private static void CopyVisualStyle(Control source, Control target)
    {
        CopyControlStyle(source, target);
        target.Scale = source.Scale;
        target.Rotation = source.Rotation;
        target.PivotOffset = source.PivotOffset;
        target.CustomMinimumSize = source.CustomMinimumSize;

        if (source is Label sourceLabel && target is Label targetLabel)
        {
            targetLabel.LabelSettings = sourceLabel.LabelSettings?.Duplicate(true) as LabelSettings ?? sourceLabel.LabelSettings;
        }
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

    private static void RemoveHoverTipNodes(Node root)
    {
        var hoverTipNodes = EnumerateDescendants(root)
            .Where(IsHoverTipNode)
            .ToList();

        foreach (var node in hoverTipNodes)
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }
    }

    private static void ClearHoverTipBindings(Node root)
    {
        foreach (var node in EnumerateDescendants(root).Prepend(root))
        {
            ClearHoverTipProperties(node);
            ClearHoverTipFields(node);
            ClearHoverTipMeta(node);
        }
    }

    private static void ClearHoverTipProperties(Node node)
    {
        if (node is Control control)
        {
            control.TooltipText = string.Empty;
        }

        var properties = node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties)
        {
            if (!property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var name = property.Name;
            if (!name.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Tooltip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (property.PropertyType == typeof(string))
                {
                    property.SetValue(node, string.Empty);
                }
                else if (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null)
                {
                    property.SetValue(node, null);
                }
            }
            catch
            {
                // Best-effort cleanup only. Some native properties are intentionally read-only.
            }
        }
    }

    private static void ClearHoverTipFields(Node node)
    {
        var fields = node.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            var name = field.Name;
            if (!name.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Tooltip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Description", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Desc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (field.FieldType == typeof(string))
                {
                    field.SetValue(node, string.Empty);
                }
                else if (!field.FieldType.IsValueType || Nullable.GetUnderlyingType(field.FieldType) is not null)
                {
                    field.SetValue(node, null);
                }
            }
            catch
            {
                // Best-effort cleanup only. Some native fields are intentionally not writable.
            }
        }
    }

    private static void UpdateNativeTextBindings(Node node, string title, string description)
    {
        UpdateNativeTextProperties(node, title, description);
        UpdateNativeTextFields(node, title, description);
    }

    private static void UpdateNativeTextProperties(Node node, string title, string description)
    {
        var properties = node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var property in properties)
        {
            if (!property.CanWrite || property.GetIndexParameters().Length != 0 || property.PropertyType != typeof(string))
            {
                continue;
            }

            var replacement = GetTextReplacementForMember(property.Name, title, description);
            if (replacement is null)
            {
                continue;
            }

            try
            {
                property.SetValue(node, replacement);
            }
            catch
            {
                // Best-effort only.
            }
        }
    }

    private static void UpdateNativeTextFields(Node node, string title, string description)
    {
        var fields = node.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            if (field.FieldType != typeof(string))
            {
                continue;
            }

            var replacement = GetTextReplacementForMember(field.Name, title, description);
            if (replacement is null)
            {
                continue;
            }

            try
            {
                field.SetValue(node, replacement);
            }
            catch
            {
                // Best-effort only.
            }
        }
    }

    private static string? GetTextReplacementForMember(string memberName, string title, string description)
    {
        if (memberName.Contains("Description", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("Desc", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("Tooltip", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("Content", StringComparison.OrdinalIgnoreCase))
        {
            return description;
        }

        if (memberName.Contains("Title", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("Label", StringComparison.OrdinalIgnoreCase)
            || memberName.Contains("Header", StringComparison.OrdinalIgnoreCase)
            || memberName.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return null;
    }

    private static void ClearHoverTipMeta(Node node)
    {
        foreach (var metaName in node.GetMetaList())
        {
            var key = metaName.ToString();
            if (!key.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("Tooltip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            node.RemoveMeta(metaName);
        }
    }

    private static Label? FindSettingsTitleLabel(Node row, Node? excludedSubtree)
    {
        return EnumerateDescendants(row)
            .OfType<Label>()
            .Where(label => !string.IsNullOrWhiteSpace(label.Text))
            .Where(label => excludedSubtree is null || !IsDescendantOf(label, excludedSubtree))
            .Where(label => !HasButtonLikeAncestor(label))
            .Where(label => !HasHoverTipAncestor(label))
            .OrderBy(label => GetControlSortX(label))
            .ThenByDescending(label => label.Text.Length)
            .FirstOrDefault()
            ?? EnumerateDescendants(row)
                .OfType<Label>()
                .Where(label => !string.IsNullOrWhiteSpace(label.Text))
                .FirstOrDefault()
            ?? FindFirstLabel(row);
    }

    private static bool IsHoverTipNode(Node node)
    {
        var name = node.Name.ToString();
        var typeName = node.GetType().Name;
        return name.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tooltip", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Tooltip", StringComparison.OrdinalIgnoreCase);
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

    private static bool HasButtonLikeAncestor(Control control)
    {
        Node? current = control;
        while (current is not null)
        {
            if (current is Control currentControl && IsButtonLike(currentControl))
            {
                return true;
            }

            current = current.GetParent();
        }

        return false;
    }

    private static bool HasHoverTipAncestor(Control control)
    {
        Node? current = control;
        while (current is not null)
        {
            var name = current.Name.ToString();
            var typeName = current.GetType().Name;
            if (name.Contains("HoverTip", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("HoverTip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.GetParent();
        }

        return false;
    }

    private static bool IsButtonLike(Control control)
    {
        var name = control.Name.ToString();
        var typeName = control.GetType().Name;
        return control is Button
            || name.Contains("Button", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Dropdown", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Button", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Dropdown", StringComparison.OrdinalIgnoreCase);
    }

    private static float GetControlSortX(Control control)
    {
        var rect = control.GetGlobalRect();
        return rect.Position.X;
    }

    private static void MakeVisualResourcesUnique(Node root)
    {
        foreach (var node in EnumerateDescendants(root).Prepend(root))
        {
            if (node is CanvasItem canvasItem && canvasItem.Material is Resource material)
            {
                canvasItem.Material = material.Duplicate(true) as Material;
            }

            if (node is Control control)
            {
                if (control.Theme is Resource theme)
                {
                    control.Theme = theme.Duplicate(true) as Theme;
                }

                if (control is Label label && label.LabelSettings is Resource labelSettings)
                {
                    label.LabelSettings = labelSettings.Duplicate(true) as LabelSettings;
                }
            }
        }
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

    private static void AnimateValueDisplay(Control valueDisplay)
    {
        if (!valueDisplay.HasMeta(ValueDisplayBaseScaleMetaName))
        {
            valueDisplay.SetMeta(ValueDisplayBaseScaleMetaName, valueDisplay.Scale);
        }

        if (!valueDisplay.HasMeta(ValueDisplayBaseModulateMetaName))
        {
            valueDisplay.SetMeta(ValueDisplayBaseModulateMetaName, valueDisplay.Modulate);
        }

        var baseScale = ((Variant)valueDisplay.GetMeta(ValueDisplayBaseScaleMetaName)).AsVector2();
        var baseModulate = ((Variant)valueDisplay.GetMeta(ValueDisplayBaseModulateMetaName)).AsColor();

        valueDisplay.Scale = baseScale * 1.08f;
        valueDisplay.Modulate = new Color(1f, 0.94f, 0.76f, baseModulate.A);

        var tween = valueDisplay.CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(valueDisplay, "scale", baseScale, 0.16);
        tween.TweenProperty(valueDisplay, "modulate", baseModulate, 0.16);
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
