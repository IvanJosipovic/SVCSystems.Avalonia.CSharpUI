using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// Maps simple names from source to control types and closes the result over base types.
/// Base-type closure is required because fluent setters are emitted on the type that declares
/// the Avalonia property: Button().Margin(...) needs an extension on Layoutable, which is a
/// base type of Button.
/// </summary>
internal static class UsageResolver
{
    internal static UsageResult Resolve(ControlCatalog catalog, ImmutableArray<string> names)
    {
        var keys = new HashSet<TypeKey>();
        var ambiguous = new SortedSet<string>(StringComparer.Ordinal);

        // Common Avalonia controls are always available, even when usage detection cannot see
        // them (compiled XAML, reflection, runtime-composed views).
        foreach (var type in catalog.CommonTypes())
        {
            AddWithClosure(type, keys);
        }

        // Local controls belong to the application itself, so their declarations are roots.
        foreach (var entry in catalog.LocalEntries)
        {
            AddWithClosure(entry.Symbol, keys);
        }

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            // An attached-property extension name (Host_Property) must root its host even when the
            // host type is never mentioned, because the generated setter applies to any control.
            if (catalog.TryGetAttachedPropertyHost(name, out var attachedHost))
            {
                if (attachedHost.IsOrInheritsFrom("Avalonia.AvaloniaObject"))
                {
                    AddWithClosure(attachedHost, keys);
                }
                else
                {
                    // Attached properties can live on a plain/static helper class. Such a host has
                    // no Avalonia base chain, but its model still emits the attached setters.
                    keys.Add(TypeKey.Create(attachedHost));
                }
            }

            var entries = catalog.Lookup(name);
            if (entries.Count == 0)
            {
                continue;
            }

            if (entries.Count == 1)
            {
                AddWithClosure(entries[0].Symbol, keys);
                continue;
            }

            // Include every candidate: preferring a local or Avalonia.Controls match would drop
            // controls that source names explicitly (e.g. Vendor.Gauge with a local Other.Gauge).
            // Never emit two extension classes with the same generated name (same namespace and
            // type name in different assemblies).
            var emittedClassNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var extensionClassName = SymbolUtilities.BuildExtensionClassName(entry.Symbol);
                if (emittedClassNames.Add(extensionClassName))
                {
                    AddWithClosure(entry.Symbol, keys);
                }
            }

            ambiguous.Add(name);
        }

        var ordered = keys
            .OrderBy(static key => key.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static key => key.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        return new UsageResult(ordered, ambiguous.ToImmutableArray());
    }

    private static void AddWithClosure(INamedTypeSymbol type, HashSet<TypeKey> keys)
    {
        INamedTypeSymbol? current = type;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            if (current.IsOrInheritsFrom("Avalonia.AvaloniaObject") &&
                current.IsAccessibleFromGeneratedCode())
            {
                keys.Add(TypeKey.Create(current));
            }

            current = current.BaseType;
        }
    }
}
