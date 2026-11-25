using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace SVCSystems.Avalonia.CSharpUI.Accessibility;

/// <summary>
/// Scans a live Avalonia control tree for common accessibility defects. The scanner deliberately
/// does not invent names: it reports surrounding context so a developer or agent can choose a
/// meaningful one.
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
            case Button button when !HasName(button) && !HasTextContent(button.Content):
                diagnostics.Add(Create(
                    "SVCUI_A11Y_001",
                    "An icon-only button has no accessible name",
                    "4.1.2 Name, Role, Value",
                    button,
                    ".Name(\"...\")",
                    parentName,
                    nearestHeading,
                    siblingText,
                    null,
                    null));
                break;

            case Image image when !HasName(image):
                diagnostics.Add(Create(
                    "SVCUI_A11Y_002",
                    "An image has no alternative text and is not marked decorative",
                    "1.1.1 Non-text Content",
                    image,
                    ".Name(\"...\")",
                    parentName,
                    nearestHeading,
                    siblingText,
                    null,
                    null));
                break;

            case TextBox textBox when !HasName(textBox):
                diagnostics.Add(Create(
                    "SVCUI_A11Y_003",
                    "A text field has no label or accessible name",
                    "3.3.2 Labels or Instructions",
                    textBox,
                    ".Name(\"...\")",
                    parentName,
                    nearestHeading,
                    siblingText ?? textBox.PlaceholderText,
                    null,
                    textBox.Text));
                break;
        }

        var nameForChildren = HasName(control) ? GetName(control) : parentName;
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
        AutomationId = control.Name,
        SuggestedModifier = modifier,
        SuggestedValue = "<<describe this control>>",
        CodeSnippet = "AutomationProperties.SetName(control, \"<<describe this control>>\");",
        ParentAutomationName = parentName,
        NearestHeading = nearestHeading,
        SiblingText = siblingText,
        ChildText = childText,
        PlaceholderText = placeholder
    };

    private static bool HasName(Control control) => !string.IsNullOrWhiteSpace(GetName(control));

    private static string? GetName(Control control) => AutomationProperties.GetName(control);

    private static bool HasTextContent(object? content) =>
        content is string text && !string.IsNullOrWhiteSpace(text);
}
