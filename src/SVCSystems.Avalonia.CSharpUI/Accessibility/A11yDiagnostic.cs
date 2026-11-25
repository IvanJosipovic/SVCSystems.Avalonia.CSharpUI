using System.Text;
using System.Text.Json;

namespace SVCSystems.Avalonia.CSharpUI.Accessibility;

/// <summary>
/// A structured accessibility finding. Context fields are intentional: an agent can generate a
/// meaningful name from sibling text and the nearest heading instead of inventing a placeholder.
/// </summary>
public sealed record A11yDiagnostic
{
    public required string Id { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string WcagCriterion { get; init; }
    public required string ElementType { get; init; }
    public string? AutomationId { get; init; }
    public string? SuggestedModifier { get; init; }
    public string? SuggestedValue { get; init; }
    public string? CodeSnippet { get; init; }
    public string? ParentAutomationName { get; init; }
    public string? NearestHeading { get; init; }
    public string? SiblingText { get; init; }
    public string? ChildText { get; init; }
    public string? PlaceholderText { get; init; }
}

/// <summary>
/// JSON export for an accessibility scan. Written with <see cref="Utf8JsonWriter"/> so it is safe
/// under trimming and NativeAOT.
/// </summary>
public static class A11yReport
{
    public static string ToJson(IReadOnlyList<A11yDiagnostic> diagnostics)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", diagnostics.Count);

            writer.WriteStartObject("bySeverity");
            foreach (var group in diagnostics.GroupBy(static diagnostic => diagnostic.Severity, StringComparer.Ordinal))
            {
                writer.WriteNumber(group.Key, group.Count());
            }

            writer.WriteEndObject();

            writer.WriteStartObject("byCheck");
            foreach (var group in diagnostics.GroupBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal))
            {
                writer.WriteNumber(group.Key, group.Count());
            }

            writer.WriteEndObject();

            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("id", diagnostic.Id);
                writer.WriteString("severity", diagnostic.Severity);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteString("wcagCriterion", diagnostic.WcagCriterion);
                writer.WriteString("elementType", diagnostic.ElementType);
                WriteOptional(writer, "automationId", diagnostic.AutomationId);
                WriteOptional(writer, "suggestedModifier", diagnostic.SuggestedModifier);
                WriteOptional(writer, "suggestedValue", diagnostic.SuggestedValue);
                WriteOptional(writer, "codeSnippet", diagnostic.CodeSnippet);
                WriteOptional(writer, "parentAutomationName", diagnostic.ParentAutomationName);
                WriteOptional(writer, "nearestHeading", diagnostic.NearestHeading);
                WriteOptional(writer, "siblingText", diagnostic.SiblingText);
                WriteOptional(writer, "childText", diagnostic.ChildText);
                WriteOptional(writer, "placeholderText", diagnostic.PlaceholderText);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
