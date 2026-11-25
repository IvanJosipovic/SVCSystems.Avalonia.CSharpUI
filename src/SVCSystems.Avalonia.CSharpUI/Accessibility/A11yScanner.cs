using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace SVCSystems.Avalonia.CSharpUI.Accessibility;

/// <summary>
/// Scans a live Avalonia control tree for common accessibility defects. The scanner deliberately
/// does not invent names: it reports surrounding context so a developer or agent can choose a
/// meaningful one. User-entered text (notably passwords) is never exported as diagnostic context.
/// </summary>
public static class A11yScanner
{
    public static IReadOnlyList<A11yDiagnostic> Scan(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var diagnostics = new List<A11yDiagnostic>();
        Walk(root, null, null, null, diagnostics);
        return diagnostics;
    }

    private static void Walk(
        Control control,
        string? parentName,
        string? nearestHeading,
        string? siblingText,
        List<A11yDiagnostic> diagnostics)
    {
        switch (control)
        {
            case Button button when !HasName(button):
                diagnostics.Add(Create(
                    "SVCUI_A11Y_001",
                    "An icon-only button has no accessible name",
                    "4.1.2 Name, Role, Value",
                    button,
                    ".Name(\"...\")",
                    parentName: parentName,
                    nearestHeading: nearestHeading,
                    placeholder: null,
                    siblingText: siblingText,
                    childText: null));
                break;

            case Image image when !HasName(image) &&
                AutomationProperties.GetAccessibilityView(image) != AccessibilityView.Raw:
                diagnostics.Add(Create(
                    "SVCUI_A11Y_002",
                    "An image has no alternative text and is not marked decorative",
                    "1.1.1 Non-text Content",
                    image,
                    ".Name(\"...\")",
                    parentName: parentName,
                    nearestHeading: nearestHeading,
                    placeholder: null,
                    siblingText: siblingText,
                    childText: null));
                break;

            case TextBox textBox when !HasName(textBox):
                diagnostics.Add(Create(
                    "SVCUI_A11Y_003",
                    "A text field has no label or accessible name",
                    "3.3.2 Labels or Instructions",
                    textBox,
                    ".Name(\"...\")",
                    parentName: parentName,
                    nearestHeading: nearestHeading,
                    placeholder: textBox.PlaceholderText,
                    siblingText: siblingText,
                    childText: null));
                break;
        }

        var effectiveName = GetEffectiveName(control);
        var nameForChildren = !string.IsNullOrWhiteSpace(effectiveName) ? effectiveName : parentName;
        var headingForChildren = control is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)
            ? text.Text
            : nearestHeading;

        var children = control.GetVisualChildren().OfType<Control>().ToList();
        var siblingTexts = children.OfType<TextBlock>()
            .Select(static text => text.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        var childSiblingText = siblingTexts.Length == 0 ? siblingText : string.Join(" ", siblingTexts);
        var childHeading = siblingTexts.Length > 0 ? siblingTexts[0] : headingForChildren;

        foreach (var child in children)
        {
            Walk(child, nameForChildren, childHeading, childSiblingText, diagnostics);
        }
    }

    private static A11yDiagnostic Create(
        string id,
        string message,
        string criterion,
        Control control,
        string modifier,
        string? parentName,
        string? nearestHeading,
        string? placeholder,
        string? siblingText,
        string? childText) => new()
    {
        Id = id,
        Severity = "Warning",
        Message = message,
        WcagCriterion = criterion,
        ElementType = control.GetType().Name,
        AutomationId = AutomationProperties.GetAutomationId(control) ?? control.Name,
        SuggestedModifier = modifier,
        SuggestedValue = "<<describe this control>>",
        CodeSnippet = "AutomationProperties.SetName(control, \"<<describe this control>>\");",
        ParentAutomationName = parentName,
        NearestHeading = nearestHeading,
        SiblingText = siblingText,
        ChildText = childText,
        PlaceholderText = placeholder
    };

    private static bool HasName(Control control) => !string.IsNullOrWhiteSpace(GetEffectiveName(control));

    /// <summary>
    /// Resolves the name a control presents to assistive technology: an explicit automation name,
    /// a name supplied by <see cref="AutomationProperties.LabeledByProperty"/>, or a visible label
    /// held as content (commonly a <see cref="TextBlock"/> inside a button or label).
    /// TextBox.Text is deliberately never consulted.
    /// </summary>
    private static string? GetEffectiveName(Control control)
    {
        var explicitName = AutomationProperties.GetName(control);
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        var labeledBy = AutomationProperties.GetLabeledBy(control);
        if (labeledBy is not null)
        {
            var labelName = GetNameFromContent(labeledBy);
            if (!string.IsNullOrWhiteSpace(labelName))
            {
                return labelName;
            }
        }

        return control is ContentControl contentControl
            ? GetNameFromContent(contentControl.Content)
            : null;
    }

    private static string? GetNameFromContent(object? content, int depth = 0)
    {
        if (depth > 4)
        {
            return null;
        }

        switch (content)
        {
            case string text:
                return text;
            case TextBlock textBlock:
                return textBlock.Text;
            case ContentControl contentControl:
                return FirstNonEmpty(
                    AutomationProperties.GetName(contentControl),
                    GetNameFromContent(contentControl.Content, depth + 1));
            case Control child when child is not TextBox:
            {
                var explicitName = AutomationProperties.GetName(child);
                if (!string.IsNullOrWhiteSpace(explicitName))
                {
                    return explicitName;
                }

                foreach (var logicalChild in child.GetLogicalChildren().OfType<Control>())
                {
                    var childName = GetNameFromContent(logicalChild, depth + 1);
                    if (!string.IsNullOrWhiteSpace(childName))
                    {
                        return childName;
                    }
                }

                return null;
            }
            default:
                return null;
        }
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;
}
