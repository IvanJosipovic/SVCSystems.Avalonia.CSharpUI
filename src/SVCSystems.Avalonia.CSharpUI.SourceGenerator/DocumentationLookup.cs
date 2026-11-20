using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator;

/// <summary>
/// Roslyn's compiler host does not attach XML documentation files to metadata references, so
/// <see cref="ISymbol.GetDocumentationCommentXml()"/> is empty for referenced controls. This
/// lookup reads the .xml file that ships next to a referenced assembly and serves its member
/// entries by documentation-comment ID. Source symbols still use the normal symbol API.
///
/// Efficiency: reference→path is resolved once per assembly identity, each XML file is parsed
/// once, and each requested entry is formatted once. None of that work is repeated per generated
/// member or per editor keystroke.
/// </summary>
internal static class DocumentationLookup
{
    private static readonly ConcurrentDictionary<string, Lazy<string?>> PathByIdentity = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> RawEntriesByPath = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Path, string Id), string> FormattedEntries = new();

    /// <summary>Returns documentation already formatted as /// lines.</summary>
    internal static string GetDocumentation(ISymbol? symbol, Compilation? compilation)
    {
        if (symbol is null)
        {
            return string.Empty;
        }

        // Source symbols carry their documentation in the symbol itself.
        if (IsSourceSymbol(symbol))
        {
            return FormatDocumentation(symbol.GetDocumentationCommentXml());
        }

        // Metadata symbols: use the XML file beside the reference when available.
        var path = compilation is null ? null : GetXmlPath(compilation, symbol.ContainingAssembly);
        if (path is not null)
        {
            var entries = RawEntriesByPath
                .GetOrAdd(path, static filePath => new Lazy<Dictionary<string, string>>(
                    () => ParseFile(filePath),
                    LazyThreadSafetyMode.ExecutionAndPublication))
                .Value;

            foreach (var id in GetCandidateIds(symbol))
            {
                if (entries.TryGetValue(id, out var raw))
                {
                    return FormattedEntries.GetOrAdd(
                        (path, id),
                        static key => FormatDocumentation(RawEntriesByPath[key.Path].Value[key.Id]));
                }
            }
        }

        // Fallback for references created from streams or with an attached documentation provider.
        return FormatDocumentation(symbol.GetDocumentationCommentXml());
    }

    private static bool IsSourceSymbol(ISymbol symbol) =>
        symbol.Locations.Length > 0 && symbol.Locations[0].IsInSource;

    private static string FormatDocumentation(string? xmlDoc)
    {
        if (string.IsNullOrWhiteSpace(xmlDoc))
        {
            return string.Empty;
        }

        var lines = xmlDoc!
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        var sb = new StringBuilder(xmlDoc.Length + (lines.Length * 5));
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("<member", StringComparison.Ordinal) ||
                trimmed.StartsWith("</member", StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append("/// ").Append(trimmed).Append(SymbolUtilities.NewLine);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> GetCandidateIds(ISymbol symbol)
    {
        var declarationId = DocumentationCommentId.CreateDeclarationId(symbol);
        if (!string.IsNullOrEmpty(declarationId))
        {
            yield return declarationId!;
        }

        // Avalonia often documents the CLR property rather than the AvaloniaProperty field.
        if (symbol is IFieldSymbol field)
        {
            var memberName = field.GetMemberName();
            var containingType = field.ContainingType?.ToDisplayString() ?? string.Empty;
            yield return $"P:{containingType}.{memberName}";
        }
    }

#pragma warning disable RS1035 // Reading the XML file beside a reference is the only way to recover metadata docs in a compiler host.
    private static Dictionary<string, string> ParseFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var document = XDocument.Load(path);
            foreach (var member in document.Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    result[name!] = member.ToString();
                }
            }
        }
        catch (Exception)
        {
            // A missing or malformed XML file simply means no generated documentation.
        }

        return result;
    }

    private static string? FindXmlPath(Compilation compilation, IAssemblySymbol? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference portableReference ||
                !SymbolEqualityComparer.Default.Equals(
                    compilation.GetAssemblyOrModuleSymbol(reference),
                    assembly) ||
                string.IsNullOrEmpty(portableReference.FilePath))
            {
                continue;
            }

            var candidate = Path.ChangeExtension(portableReference.FilePath, ".xml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
#pragma warning restore RS1035

    private static string? GetXmlPath(Compilation compilation, IAssemblySymbol? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        // Assembly identity is stable across compilations, so this survives editor keystrokes.
        var key = assembly.Identity.GetDisplayName();
        return PathByIdentity
            .GetOrAdd(key, _ => new Lazy<string?>(
                () => FindXmlPath(compilation, assembly),
                LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }
}
