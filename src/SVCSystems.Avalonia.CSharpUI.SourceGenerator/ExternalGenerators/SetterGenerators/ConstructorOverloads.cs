using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.SetterGenerators;

/// <summary>
/// Constructor argument fragments for a value type. Both the value-setter and style-setter
/// overload generators need them for every property of that type, so they are computed once per
/// symbol instead of once per generated member.
/// </summary>
internal static class ConstructorOverloads
{
    private sealed class CacheEntry
    {
        internal ImmutableArray<(string Definitions, string Values)> Overloads = ImmutableArray<(string, string)>.Empty;
    }

    private static readonly ConditionalWeakTable<ITypeSymbol, CacheEntry> Cache = new();

    internal static ImmutableArray<(string Definitions, string Values)> Get(ITypeSymbol valueType) =>
        Cache.GetValue(valueType, static type =>
        {
            var builder = ImmutableArray.CreateBuilder<(string, string)>();
            foreach (var constructor in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (constructor.MethodKind != MethodKind.Constructor || constructor.Parameters.Length == 0)
                {
                    continue;
                }

                var parameters = constructor.Parameters;
                var definitions = new string[parameters.Length];
                var values = new string[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    definitions[i] = parameter.Type.GetFullTypeName() + " " + parameter.Name;
                    values[i] = parameter.Name;
                }

                builder.Add((string.Join(", ", definitions), string.Join(", ", values)));
            }

            return new CacheEntry { Overloads = builder.ToImmutable() };
        }).Overloads;
}
