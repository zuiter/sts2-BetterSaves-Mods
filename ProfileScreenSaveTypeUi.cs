using Godot;

namespace BetterSaves;

internal static class ProfileScreenSaveTypeUi
{
    private const string BadgeContainerName = "BetterSavesSaveTypeBadge";

    public static void InstallInProfileButton(Node? node)
    {
        if (node is not Control root)
        {
            return;
        }

        if (root.GetNodeOrNull<Control>(BadgeContainerName) is not null)
        {
            RefreshExistingBadge(root);
            return;
        }

        var badge = CreateBadge(root);
        root.AddChild(badge);
        root.MoveChild(badge, root.GetChildCount() - 1);
    }

    private static void RefreshExistingBadge(Control root)
    {
        if (root.GetNodeOrNull<Control>(BadgeContainerName) is not Control badge)
        {
            return;
        }

        var label = FindBadgeLabel(badge);
        if (label is null)
        {
            return;
        }

        label.Text = BetterSavesLocalization.GetActiveSaveTypeBadgeText();
        label.SelfModulate = BetterSavesLocalization.GetActiveSaveTypeBadgeColor();
    }

    private static Control CreateBadge(Control root)
    {
        var templateLabel = FindFirstLabel(root);
        var overlay = new MarginContainer
        {
            Name = BadgeContainerName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.AddThemeConstantOverride("margin_left", 0);
        overlay.AddThemeConstantOverride("margin_top", 8);
        overlay.AddThemeConstantOverride("margin_right", 18);
        overlay.AddThemeConstantOverride("margin_bottom", 0);

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        var label = new Label
        {
            Text = BetterSavesLocalization.GetActiveSaveTypeBadgeText(),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            SelfModulate = BetterSavesLocalization.GetActiveSaveTypeBadgeColor()
        };

        if (templateLabel is not null)
        {
            label.Theme = templateLabel.Theme;
            label.ThemeTypeVariation = templateLabel.ThemeTypeVariation;
            label.LabelSettings = templateLabel.LabelSettings;
            label.Modulate = templateLabel.Modulate;
        }

        // Make the badge slightly smaller than the slot title so it reads as metadata.
        label.Scale = new Vector2(0.78f, 0.78f);

        row.AddChild(label);
        overlay.AddChild(row);
        return overlay;
    }

    private static Label? FindBadgeLabel(Node root)
    {
        return EnumerateDescendants(root).OfType<Label>().FirstOrDefault();
    }

    private static Label? FindFirstLabel(Node root)
    {
        return EnumerateDescendants(root).OfType<Label>().FirstOrDefault();
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
