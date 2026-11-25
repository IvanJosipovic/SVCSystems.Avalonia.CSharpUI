using Avalonia.Styling;
using System;
using System.Runtime.CompilerServices;

namespace SVCSystems.Avalonia.CSharpUI;
/// <summary>
/// Typed style to support method chains with generic arguments
/// </summary>
/// <typeparam name="TControl">Type of the control that style will be applied to</typeparam>
public class Style<TControl> : Style, IRelativeStyle
    where TControl : StyledElement
{
    private Func<Selector, Selector> SelectorFunc { get; }

    /// <summary>
    /// Creates a style with an added <c>.OfType&lt;TControl&gt;()</c> selector.
    /// </summary>
    public Style()
    {
        SelectorFunc = s => s.OfType<TControl>();

        UpdateSelector(null);
    }

    /// <summary>
    /// Creates a style with the supplied selector function.
    /// </summary>
    /// <param name="selectorFunc">
    /// A selector builder. Avalonia's <c>Selectors.*</c> extension methods accept a <c>null</c>
    /// root, which the constructor probes to inspect the resulting selector string.
    /// </param>
    /// <param name="expression">The caller's selector expression, used as a heuristic fallback.</param>
    /// <param name="callerFile">Caller file path supplied by the compiler.</param>
    /// <param name="treatAsTraversal">
    /// Overrides the traversal heuristic. <c>true</c> preserves the selector as-is (no
    /// <c>.OfType&lt;TControl&gt;()</c> is added), <c>false</c> treats it as a simple selector and
    /// binds it to <typeparamref name="TControl"/>. <c>null</c> (the default) auto-detects.
    /// </param>
    /// <remarks>
    /// By default the constructor decides whether <paramref name="selectorFunc"/> already carries a
    /// root type or is a traversal by inspecting the selector string produced from a null root:
    /// a string starting with a letter is treated as an explicit root (e.g.
    /// <c>OfType&lt;StackPanel&gt;()</c>), and spaces, <c>&gt;</c>, <c>/</c> or traversal helper
    /// calls (<c>.Descendant(</c>, <c>.Child(</c>, <c>.Template(</c>) in the selector or the
    /// captured <paramref name="expression"/> are treated as a traversal. Simple selectors such
    /// as <c>.my-class</c> or <c>:pointerover</c> are additionally constrained to
    /// <typeparamref name="TControl"/> so they do not leak to unrelated control types. The string
    /// probes are a heuristic: pass <paramref name="treatAsTraversal"/> explicitly when a selector
    /// is classified incorrectly.
    /// </remarks>
    public Style(
            Func<Selector, Selector> selectorFunc,
            [CallerArgumentExpression(nameof(selectorFunc))] string? expression = null,
            [CallerFilePath] string? callerFile = null,
            bool? treatAsTraversal = null)
    {
        ArgumentNullException.ThrowIfNull(selectorFunc);

        var selectorString = TryGetSelectorString(selectorFunc);

        // 1. Check if the user explicitly specified a root (starts with a letter, e.g. OfType<StackPanel>).
        bool hasExplicitRootType = !string.IsNullOrEmpty(selectorString) && char.IsLetter(selectorString[0]);

        // 2. Check whether the selector is a traversal (a path to descendants). The explicit
        //    override wins; otherwise use the string/expression heuristic described above.
        bool isTraversal = treatAsTraversal ?? IsTraversal(selectorString, expression);

        // If a root type is explicitly specified OR the selector is a traversal, do not modify it.
        if (hasExplicitRootType || isTraversal)
        {
            SelectorFunc = selectorFunc;
        }
        else
        {
            // For simple single selectors (e.g. `.my-class` or `:pointerover`),
            // safely bind the selector to `TControl` so it won't apply to other control types.
            SelectorFunc = s => selectorFunc(s.OfType<TControl>());
        }

        UpdateSelector(null);
    }

    /// <summary>
    /// Rebuilds the style selector, optionally under a parent selector supplied by a style group.
    /// </summary>
    public void UpdateSelector(Func<Selector, Selector>? baseSelectorFunc)
    {
        try
        {
            Selector = baseSelectorFunc is not null
                ? SelectorFunc(baseSelectorFunc(null!))
                : SelectorFunc(null!);
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentNullException)
        {
            // A selector function that dereferences a null root cannot be composed. Fail with
            // an actionable message instead of a bare NullReferenceException.
            throw new ViewBuildingException(
                $"Cannot build the selector for Style<{typeof(TControl).Name}> from a null root. " +
                "Use Avalonia's Selectors.* combinators, which accept null, or supply a root selector through UpdateSelector.",
                ex);
        }
    }

    private static bool IsTraversal(string? selectorString, string? expression)
    {
        // In Avalonia traversals include spaces, '>', or '/' in the selector string.
        var fromSelector = !string.IsNullOrEmpty(selectorString) &&
            (selectorString!.Contains(' ') || selectorString.Contains('>') || selectorString.Contains('/'));

        // Also inspect the C# expression for traversal helpers for robustness.
        var fromExpression = expression is not null &&
            (expression.Contains(".Descendant(", StringComparison.Ordinal) ||
             expression.Contains(".Child(", StringComparison.Ordinal) ||
             expression.Contains(".Template(", StringComparison.Ordinal) ||
             expression.Contains(".DescendantOrSelf(", StringComparison.Ordinal) ||
             expression.Contains(".InTemplate(", StringComparison.Ordinal));

        return fromSelector || fromExpression;
    }

    /// <summary>
    /// Probes the selector function with a null root. Some functions do not tolerate null, so
    /// the heuristic falls back to the captured expression in that case.
    /// </summary>
    private static string? TryGetSelectorString(Func<Selector, Selector> selectorFunc)
    {
        try
        {
            return selectorFunc(null!)?.ToString();
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentNullException)
        {
            return null;
        }
    }
}

internal interface IRelativeStyle : IStyle
{
    void UpdateSelector(Func<Selector, Selector>? baseSelectorFunc);
}
