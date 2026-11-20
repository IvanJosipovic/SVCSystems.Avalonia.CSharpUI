using System.Text;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>
/// Describes how a compilation participates in shared public markup extensions.
/// </summary>
internal sealed class ExtensionPublishingPolicy
{
    internal ExtensionPublishingPolicy(
        bool isPublisher,
        HashSet<TypeKey>? restrictedRoots,
        string assemblyName)
    {
        IsPublisher = isPublisher;
        RestrictedRoots = restrictedRoots;
        AssemblyName = assemblyName;
    }

    /// <summary>Whether this assembly opted in to publishing its generated extensions.</summary>
    internal bool IsPublisher { get; }

    /// <summary>The current assembly's name, used as the origin of locally published controls.</summary>
    internal string AssemblyName { get; }

    /// <summary>Control keys already published by assemblies referenced by this compilation.</summary>
    internal HashSet<string> ReferencedCoverage { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The assembly that actually defines the public extensions for each covered key. Keys that
    /// are only propagated through publisher chains keep the origin of the original publisher.
    /// </summary>
    internal Dictionary<string, string> CoverageOrigins { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys claimed by more than one independent publisher. A consumer of both assemblies would
    /// see duplicate public extension methods and fail with CS0121.
    /// </summary>
    internal HashSet<string> OverlappingPublisherKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When the opt-in attribute names controls explicitly, only those controls and their base
    /// types are published. Otherwise all generated controls are published.
    /// </summary>
    internal HashSet<TypeKey>? RestrictedRoots { get; }
}

/// <summary>
/// Reads the publishing opt-in from the current assembly and the coverage metadata from its
/// references. All comparisons use stable <see cref="TypeKey"/> strings so incremental generator
/// values never hold Roslyn symbols.
/// </summary>
internal static class ExtensionPublishing
{
    private const string PublishAttributeName =
        "SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensionsAttribute";

    private const string CoverageAttributeName =
        "SVCSystems.Avalonia.CSharpUI.PublicMarkupExtensionControlsAttribute";

    // Coverage entries are emitted as "originPublisher::TypeKey". TypeKey itself contains '|',
    // so this separator keeps the defining publisher distinguishable from propagated coverage.
    private const string CoverageOriginSeparator = "::";

    internal static ExtensionPublishingPolicy Read(Compilation compilation)
    {
        var assemblyName = compilation.Assembly.Identity.Name ?? string.Empty;
        var policy = new ExtensionPublishingPolicy(
            isPublisher: false, restrictedRoots: null, assemblyName: assemblyName);

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (!IsAttribute(attribute, PublishAttributeName))
            {
                continue;
            }

            policy = new ExtensionPublishingPolicy(
                isPublisher: true,
                restrictedRoots: ReadRootControls(attribute),
                assemblyName: assemblyName);
            break;
        }

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
            {
                continue;
            }

            foreach (var attribute in assembly.GetAttributes())
            {
                if (!IsAttribute(attribute, CoverageAttributeName))
                {
                    continue;
                }

                foreach (var entry in ReadCoverageEntries(attribute, assembly.Name))
                {
                    policy.ReferencedCoverage.Add(entry.Key);

                    if (policy.CoverageOrigins.TryGetValue(entry.Key, out var existingOrigin))
                    {
                        // The same origin seen through several references is just propagation.
                        if (!string.Equals(existingOrigin, entry.Origin, StringComparison.Ordinal))
                        {
                            policy.OverlappingPublisherKeys.Add(entry.Key);
                        }
                    }
                    else
                    {
                        policy.CoverageOrigins.Add(entry.Key, entry.Origin);
                    }
                }
            }
        }

        return policy;
    }

    /// <summary>
    /// Creates the coverage attribute emitted into a publishing assembly. The attribute is placed
    /// at file scope because assembly attributes cannot appear inside a namespace.
    /// </summary>
    internal static string CreateCoverageSource(IReadOnlyDictionary<string, string> keysWithOrigins)
    {
        var entries = keysWithOrigins
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"\"{Escape(pair.Value + CoverageOriginSeparator + pair.Key)}\"");
        var source = new StringBuilder(512);
        source.AppendLine("// <auto-generated />");
        source.AppendLine("[assembly: global::SVCSystems.Avalonia.CSharpUI.PublicMarkupExtensionControlsAttribute(" +
            string.Join(", ", entries) + ")]");
        return source.ToString();
    }

    private static bool IsAttribute(AttributeData attribute, string metadataName) =>
        attribute.AttributeClass?.ToDisplayString() == metadataName;

    private static HashSet<TypeKey>? ReadRootControls(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
        {
            return null;
        }

        var roots = new HashSet<TypeKey>();
        foreach (var value in attribute.ConstructorArguments[0].Values)
        {
            if (value.Value is not INamedTypeSymbol type)
            {
                continue;
            }

            var current = type;
            while (current is not null && current.SpecialType != SpecialType.System_Object)
            {
                roots.Add(TypeKey.Create(current));
                current = current.BaseType;
            }
        }

        return roots.Count == 0 ? null : roots;
    }

    private static IEnumerable<(string Origin, string Key)> ReadCoverageEntries(
        AttributeData attribute,
        string fallbackOrigin)
    {
        if (attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
        {
            yield break;
        }

        foreach (var value in attribute.ConstructorArguments[0].Values)
        {
            if (value.Value is not string text || text.Length == 0)
            {
                continue;
            }

            var separator = text.IndexOf(CoverageOriginSeparator, StringComparison.Ordinal);
            if (separator < 0)
            {
                // Coverage written before origins were recorded: the assembly carrying the
                // attribute is the only possible definer.
                yield return (fallbackOrigin, text);
                continue;
            }

            var origin = text.Substring(0, separator);
            var key = text.Substring(separator + CoverageOriginSeparator.Length);
            if (origin.Length > 0 && key.Length > 0)
            {
                yield return (origin, key);
            }
        }
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");
}
