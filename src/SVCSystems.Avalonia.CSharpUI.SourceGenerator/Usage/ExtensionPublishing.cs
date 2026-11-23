using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

/// <summary>Coverage data derived only from referenced assemblies; safe to key by reference hash.</summary>
internal sealed class ReferencedCoverageData
{
    internal HashSet<string> ReferencedCoverage { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> CoverageOrigins { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> OverlappingPublisherKeys { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Describes how a compilation participates in shared public markup extensions.
/// </summary>
internal sealed class ExtensionPublishingPolicy
{
    internal ExtensionPublishingPolicy(
        bool isPublisher,
        HashSet<TypeKey>? restrictedRoots,
        string assemblyName,
        ReferencedCoverageData coverage)
    {
        IsPublisher = isPublisher;
        RestrictedRoots = restrictedRoots;
        AssemblyName = assemblyName;
        ReferencedCoverage = coverage.ReferencedCoverage;
        CoverageOrigins = coverage.CoverageOrigins;
        OverlappingPublisherKeys = coverage.OverlappingPublisherKeys;
    }

    /// <summary>Whether this assembly opted in to publishing its generated extensions.</summary>
    internal bool IsPublisher { get; }

    /// <summary>The current assembly's name, used as the origin of locally published controls.</summary>
    internal string AssemblyName { get; }

    /// <summary>Control keys already published by assemblies referenced by this compilation.</summary>
    internal HashSet<string> ReferencedCoverage { get; }

    /// <summary>
    /// The assembly that actually defines the public extensions for each covered key. Keys that
    /// are only propagated through publisher chains keep the origin of the original publisher.
    /// </summary>
    internal Dictionary<string, string> CoverageOrigins { get; }

    /// <summary>
    /// Keys claimed by more than one independent publisher. A consumer of both assemblies would
    /// see duplicate public extension methods and fail with CS0121.
    /// </summary>
    internal HashSet<string> OverlappingPublisherKeys { get; }

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

    private const string PublishAttributeShortName = "GeneratePublicMarkupExtensionsAttribute";
    private const string CoverageAttributeShortName = "PublicMarkupExtensionControlsAttribute";

    // Coverage entries are emitted as "originPublisher::TypeKey". TypeKey itself contains '|',
    // so this separator keeps the defining publisher distinguishable from propagated coverage.
    private const string CoverageOriginSeparator = "::";

    // Legacy entries have no origin. Their origin stays unknown through propagation so they still
    // suppress local generation but never trigger a false overlap diagnostic.
    private const string UnknownOrigin = "";

    // Only data derived from referenced assemblies is cached by reference hash. Local opt-in
    // settings are always read from the current compilation so they cannot become stale.
    private static readonly ConcurrentDictionary<ulong, ReferencedCoverageData> CoverageCache = new();

    internal static ExtensionPublishingPolicy Read(
        Compilation compilation,
        CancellationToken cancellationToken = default,
        ulong referenceHash = 0)
    {
        ReferencedCoverageData coverage;
        if (referenceHash != 0 && CoverageCache.TryGetValue(referenceHash, out var cached))
        {
            coverage = cached;
        }
        else
        {
            coverage = ReadReferencedCoverage(compilation, cancellationToken);
            if (referenceHash != 0)
            {
                if (CoverageCache.Count > 8)
                {
                    CoverageCache.Clear();
                }

                coverage = CoverageCache.GetOrAdd(referenceHash, coverage);
            }
        }

        var assemblyName = compilation.Assembly.Identity.Name ?? string.Empty;
        var isPublisher = false;
        HashSet<TypeKey>? restrictedRoots = null;
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (!IsAttribute(attribute, PublishAttributeName, PublishAttributeShortName))
            {
                continue;
            }

            isPublisher = true;
            restrictedRoots = ReadRootControls(attribute);
            break;
        }

        return new ExtensionPublishingPolicy(isPublisher, restrictedRoots, assemblyName, coverage);
    }

    private static ReferencedCoverageData ReadReferencedCoverage(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var coverage = new ReferencedCoverageData();
        foreach (var reference in compilation.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly ||
                SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
            {
                continue;
            }

            foreach (var attribute in assembly.GetAttributes())
            {
                if (!IsAttribute(attribute, CoverageAttributeName, CoverageAttributeShortName))
                {
                    continue;
                }

                foreach (var entry in ReadCoverageEntries(attribute))
                {
                    coverage.ReferencedCoverage.Add(entry.Key);

                    if (coverage.CoverageOrigins.TryGetValue(entry.Key, out var existingOrigin))
                    {
                        // The same origin through several references is propagation. An unknown
                        // (legacy) origin cannot be attributed, so it is excluded from the check.
                        if (existingOrigin != UnknownOrigin &&
                            entry.Origin != UnknownOrigin &&
                            !string.Equals(existingOrigin, entry.Origin, StringComparison.Ordinal))
                        {
                            coverage.OverlappingPublisherKeys.Add(entry.Key);
                        }

                        if (existingOrigin == UnknownOrigin && entry.Origin != UnknownOrigin)
                        {
                            coverage.CoverageOrigins[entry.Key] = entry.Origin;
                        }
                    }
                    else
                    {
                        coverage.CoverageOrigins.Add(entry.Key, entry.Origin);
                    }
                }
            }
        }

        return coverage;
    }

    /// <summary>
    /// Creates the coverage attribute emitted into a publishing assembly. The attribute is placed
    /// at file scope because assembly attributes cannot appear inside a namespace.
    /// </summary>
    internal static string CreateCoverageSource(IReadOnlyDictionary<string, string> keysWithOrigins)
    {
        var entries = keysWithOrigins
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value == UnknownOrigin
                ? $"\"{Escape(pair.Key)}\""
                : $"\"{Escape(pair.Value + CoverageOriginSeparator + pair.Key)}\"");
        var source = new StringBuilder(512);
        source.AppendLine("// <auto-generated />");
        source.AppendLine("[assembly: global::SVCSystems.Avalonia.CSharpUI.PublicMarkupExtensionControlsAttribute(" +
            string.Join(", ", entries) + ")]");
        return source.ToString();
    }

    private static bool IsAttribute(AttributeData attribute, string metadataName, string shortName)
    {
        var attributeClass = attribute.AttributeClass;
        return attributeClass is not null &&
               attributeClass.Name == shortName &&
               attributeClass.ToDisplayString() == metadataName;
    }

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

    private static IEnumerable<(string Origin, string Key)> ReadCoverageEntries(AttributeData attribute)
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

            // A TypeKey can itself contain "::" (fully qualified generic arguments such as
            // "global::Model"), so only a separator before the assembly/type delimiter '|'
            // introduces an origin prefix.
            var keyDelimiter = text.IndexOf('|');
            var originSeparator = text.IndexOf(CoverageOriginSeparator, StringComparison.Ordinal);
            if (originSeparator < 0 || keyDelimiter <= originSeparator)
            {
                yield return (UnknownOrigin, text);
                continue;
            }

            var origin = text.Substring(0, originSeparator);
            var key = text.Substring(originSeparator + CoverageOriginSeparator.Length);
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
