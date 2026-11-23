using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// Reads the curated common-control list from <c>[assembly: GenerateMarkupControl(typeof(...))]</c>.
/// The runtime library declares the built-in list with typeof() (compile-time checked); the
/// generator reads symbols from metadata only, since it targets netstandard2.0 and Avalonia 12
/// does not. The application's own assembly is read too, which makes the attribute a narrow
/// per-type opt-in for controls that usage detection cannot see (e.g. compiled XAML only).
/// </summary>
internal static class CommonControls
{
    private const string AttributeName = "SVCSystems.Avalonia.CSharpUI.GenerateMarkupControlAttribute";
    private const string AttributeShortName = "GenerateMarkupControlAttribute";

    private sealed class Names
    {
        internal string[] Values = [];
    }

    // Metadata assembly symbols are reused across compilations with the same references; decoding
    // their attributes once removes another full pass over every reference on each generation.
    private static readonly ConditionalWeakTable<IAssemblySymbol, Names> Cache = new();

    internal static HashSet<string> GetTypeNames(Compilation compilation)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddTypes(compilation.Assembly.GetAttributes(), names);

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
            {
                // Source assembly symbols change every compilation; read them directly.
                AddTypes(assembly.GetAttributes(), names);
                continue;
            }

            var cached = Cache.GetValue(assembly, static value =>
            {
                var local = new HashSet<string>(StringComparer.Ordinal);
                AddTypes(value.GetAttributes(), local);
                return new Names { Values = [.. local] };
            });

            foreach (var name in cached.Values)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static void AddTypes(System.Collections.Generic.IEnumerable<AttributeData> attributes, HashSet<string> names)
    {
        foreach (var attribute in attributes)
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null ||
                attributeClass.Name != AttributeShortName ||
                attributeClass.ToDisplayString() != AttributeName ||
                attribute.ConstructorArguments.Length == 0)
            {
                continue;
            }

            foreach (var argument in attribute.ConstructorArguments[0].Values)
            {
                if (argument.Value is not INamedTypeSymbol type)
                {
                    continue;
                }

                var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (typeName.StartsWith("global::", StringComparison.Ordinal))
                {
                    typeName = typeName.Substring("global::".Length);
                }

                names.Add(typeName);
            }
        }
    }
}
