using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

internal readonly struct GeneratedSource : IEquatable<GeneratedSource>
{
    private readonly int _hash;

    internal GeneratedSource(string hintName, string text)
    {
        HintName = hintName;
        Text = text;
        unchecked
        {
            _hash = ((hintName?.GetHashCode() ?? 0) * 397) ^ (text?.GetHashCode() ?? 0);
        }
    }

    internal string HintName { get; }

    internal string Text { get; }

    public bool Equals(GeneratedSource other) =>
        string.Equals(HintName, other.HintName, StringComparison.Ordinal) &&
        string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GeneratedSource other && Equals(other);

    public override int GetHashCode() => _hash;
}

internal readonly struct GenerationDiagnostic : IEquatable<GenerationDiagnostic>
{
    private readonly int _hash;

    internal GenerationDiagnostic(string id, string message, DiagnosticSeverity severity, string? locationMarker = null)
    {
        Id = id;
        Message = message;
        Severity = severity;
        LocationMarker = locationMarker;
        unchecked
        {
            var hash = (id?.GetHashCode() ?? 0) * 397 ^ (message?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ (int)severity;
            _hash = (hash * 397) ^ (locationMarker?.GetHashCode() ?? 0);
        }
    }

    internal string Id { get; }

    internal string Message { get; }

    internal DiagnosticSeverity Severity { get; }

    /// <summary>
    /// A declaration id, not a <see cref="Location"/>: cached results may be reported by a later
    /// compilation whose syntax trees are different objects. The current tree is found at report
    /// time from this id.
    /// </summary>
    internal string? LocationMarker { get; }

    public bool Equals(GenerationDiagnostic other) =>
        string.Equals(Id, other.Id, StringComparison.Ordinal) &&
        string.Equals(Message, other.Message, StringComparison.Ordinal) &&
        Severity == other.Severity &&
        string.Equals(LocationMarker, other.LocationMarker, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GenerationDiagnostic other && Equals(other);

    public override int GetHashCode() => _hash;
}

/// <summary>
/// The complete generated output for one compilation. Equality is based on the generated text, so
/// Roslyn caches the registered source output across edits that don't change the generated APIs.
/// </summary>
internal sealed class GenerationResult
{
    private readonly int _hash;

    internal GenerationResult(
        ImmutableArray<GeneratedSource> sources,
        ImmutableArray<GenerationDiagnostic> diagnostics)
    {
        Sources = sources;
        Diagnostics = diagnostics;

        unchecked
        {
            var hash = 17;
            foreach (var source in sources)
            {
                hash = (hash * 31) + source.GetHashCode();
            }

            foreach (var diagnostic in diagnostics)
            {
                hash = (hash * 31) + diagnostic.GetHashCode();
            }

            _hash = hash;
        }
    }

    internal ImmutableArray<GeneratedSource> Sources { get; }

    internal ImmutableArray<GenerationDiagnostic> Diagnostics { get; }

    public override int GetHashCode() => _hash;
}

internal sealed class GenerationResultComparer : IEqualityComparer<GenerationResult>
{
    internal static readonly GenerationResultComparer Instance = new();

    public bool Equals(GenerationResult? x, GenerationResult? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null &&
         x.GetHashCode() == y.GetHashCode() &&
         x.Sources.SequenceEqual(y.Sources) &&
         x.Diagnostics.SequenceEqual(y.Diagnostics));

    public int GetHashCode(GenerationResult result) => result.GetHashCode();
}

/// <summary>
/// Generates fluent extensions, control factories and factory registrations for the controls
/// that the application actually mentions in source (plus their base-type closure).
/// </summary>
public sealed class UsageDrivenMarkupGenerator
{
    private const string GlobalFactoriesAttributeName =
        "SVCSystems.Avalonia.CSharpUI.GenerateGlobalControlFactoriesAttribute";

    private sealed class TextBox
    {
        internal string Text = string.Empty;
    }

    // Metadata control symbols are reused across compilations; caching the emitted text per
    // symbol avoids rebuilding dozens of factory methods and registrations on every edit.
    private static readonly ConditionalWeakTable<INamedTypeSymbol, TextBox> FactoryMethodCache = new();
    private static readonly ConditionalWeakTable<INamedTypeSymbol, TextBox> RegistrationCache = new();

    private static readonly ExternalGeneratorHost SharedHost = new();
    private static readonly ConcurrentDictionary<string, DiagnosticDescriptor> Descriptors = new(StringComparer.Ordinal);

    internal static readonly DiagnosticDescriptor ExtensionGenerationFailed = new(
        id: "SVCUIGEN003",
        title: "Avalonia extension generation failed",
        messageFormat: "Could not generate extensions for '{0}': {1}",
        category: "SVCSystems.Avalonia.CSharpUI",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The source generator encountered an error while analyzing or emitting the control API.");

    internal static readonly DiagnosticDescriptor AmbiguousControlName = new(
        id: "SVCUIGEN006",
        title: "Ambiguous control name",
        messageFormat: "The control name '{0}' matches multiple control types; extensions were generated for all of them",
        category: "SVCSystems.Avalonia.CSharpUI",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A control name used in source matched more than one type. All matches were generated so code compiles regardless of which type was meant.");

    internal static readonly DiagnosticDescriptor CollidingFactoryName = new(
        id: "SVCUIGEN009",
        title: "Colliding control factory name",
        messageFormat: "Control factory name '{0}' is shared by {1} controls; no factory was generated. Set Alias on one control or construct the control explicitly.",
        category: "SVCSystems.Avalonia.CSharpUI",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two or more controls resolve to the same generated factory name. Factories for all of them are suppressed; registrations are still emitted.");

    internal static readonly DiagnosticDescriptor OverlappingPublishers = new(
        id: "SVCUIGEN008",
        title: "Overlapping markup extension publishers",
        messageFormat: "These controls are published by more than one independent assembly: {0}. Publish them from a single shared upstream library so consumers do not get ambiguous extension methods.",
        category: "SVCSystems.Avalonia.CSharpUI",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two referenced assemblies both publish public extensions for the same control. Remove one publisher or move the shared extensions to a single upstream library.");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var names = UsageNameProvider.Create(context).Collect();
        var input = names.Combine(context.CompilationProvider);

        // Produce an equatable result first. Registering the source output on that result (instead
        // of on the compilation) lets Roslyn cache it when an unrelated edit leaves generated text
        // unchanged, instead of re-adding every file on each keystroke.
        var generation = input
            .Select(static (pair, cancellationToken) => Generate(pair.Left, pair.Right, cancellationToken))
            .WithComparer(GenerationResultComparer.Instance)
            .WithTrackingName("UsageDriven.Generate");

        context.RegisterSourceOutput(generation, static (sourceContext, result) =>
        {
            foreach (var source in result.Sources)
            {
                sourceContext.AddSource(source.HintName, SourceText.From(source.Text, Encoding.UTF8));
            }
        });

        // Diagnostics need the current compilation: a cached result may come from an earlier
        // compilation and can only be re-anchored to a syntax tree that belongs to this one.
        context.RegisterSourceOutput(
            generation.Combine(context.CompilationProvider),
            static (sourceContext, pair) =>
            {
                foreach (var diagnostic in pair.Left.Diagnostics)
                {
                    var descriptor = Descriptors.GetOrAdd(
                        diagnostic.Id + "\0" + diagnostic.Message,
                        _ => new DiagnosticDescriptor(
                            diagnostic.Id,
                            diagnostic.Id,
                            diagnostic.Message,
                            "SVCSystems.Avalonia.CSharpUI",
                            diagnostic.Severity,
                            isEnabledByDefault: true));
                    sourceContext.ReportDiagnostic(Diagnostic.Create(
                        descriptor,
                        ResolveDiagnosticLocation(diagnostic.LocationMarker, pair.Right)));
                }
            });
    }

    private static Location ResolveDiagnosticLocation(string? locationMarker, Compilation compilation)
    {
        if (locationMarker is null)
        {
            return Location.None;
        }

        var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(locationMarker, compilation);
        if (symbol is not null)
        {
            foreach (var location in symbol.Locations)
            {
                if (location.IsInSource)
                {
                    return location;
                }
            }
        }

        return Location.None;
    }

    private static GenerationResult Generate(
        ImmutableArray<string> names,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        // The compilation object changes on every keystroke, but the generated output usually
        // does not. Fingerprinting the semantic inputs lets us reuse the previous result for
        // comment/whitespace-only edits and avoid the full catalog + emit pipeline.
        var fingerprint = GenerationFingerprint.Create(names, compilation, cancellationToken);
        return GenerationCache.GetOrAdd(
            fingerprint,
            () => GenerateCore(names, compilation, cancellationToken));
    }

    private static GenerationResult GenerateCore(
        ImmutableArray<string> names,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sources = new List<GeneratedSource>();
        var diagnostics = new List<GenerationDiagnostic>();

        var referenceHash = GenerationCache.GetReferenceHash(compilation, cancellationToken);
        var catalog = ControlCatalog.Build(compilation, cancellationToken, referenceHash);
        var usage = UsageResolver.Resolve(catalog, names, cancellationToken, referenceHash);
        var publishing = ExtensionPublishing.Read(compilation, cancellationToken, referenceHash);

        if (publishing.OverlappingPublisherKeys.Count > 0)
        {
            var overlappingKeys = publishing.OverlappingPublisherKeys
                .OrderBy(static key => key, StringComparer.Ordinal);
            diagnostics.Add(new GenerationDiagnostic(
                "SVCUIGEN008",
                string.Format(OverlappingPublishers.MessageFormat.ToString(), string.Join(", ", overlappingKeys)),
                DiagnosticSeverity.Warning));
        }

        // Controls whose extensions are already public in a referenced assembly are removed from
        // this compilation's local generation. That keeps friend assemblies from seeing duplicate
        // internal extension methods and avoids recompiling the same generated APIs in each project.
        // RestrictedRoots only limits which controls are published; every uncovered control must
        // still be generated locally so its setters remain available in this compilation.
        var extensionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in usage.Keys)
        {
            var keyText = key.ToString();
            if (publishing.ReferencedCoverage.Contains(keyText))
            {
                continue;
            }

            extensionKeys.Add(keyText);
        }

        foreach (var ambiguousName in usage.AmbiguousNames)
        {
            diagnostics.Add(new GenerationDiagnostic(
                "SVCUIGEN006",
                string.Format(AmbiguousControlName.MessageFormat.ToString(), ambiguousName),
                DiagnosticSeverity.Info));
        }

        var models = new List<ControlModel>();
        var publishedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in publishing.CoverageOrigins)
        {
            publishedKeys[pair.Key] = pair.Value;
        }

        foreach (var key in usage.Keys)
        {
            if (!catalog.TryGetByKey(key, out var symbol))
            {
                continue;
            }

            ControlModel model;
            try
            {
                model = ControlModelBuilder.Build(symbol, compilation.Assembly);
            }
            catch (Exception exception)
            {
                AddFailure(diagnostics, symbol, exception);
                continue;
            }

            if (!model.IgnoreGeneratedExtensions)
            {
                models.Add(model);
            }
        }

        // When both an open generic definition and a constructed version are in the set, the
        // constructed one is what source actually uses. Emitting both creates duplicate extension
        // classes with the same generated name.
        for (var index = models.Count - 1; index >= 0; index--)
        {
            var model = models[index];
            if (!model.Type.IsGenericType ||
                model.Type.TypeArguments.All(static argument => argument.TypeKind != TypeKind.TypeParameter))
            {
                continue;
            }

            if (models.Any(candidate =>
                    !ReferenceEquals(candidate, model) &&
                    SymbolEqualityComparer.Default.Equals(candidate.Type.OriginalDefinition, model.Type.OriginalDefinition)))
            {
                models.RemoveAt(index);
            }
        }

        // A factory name shared by two controls cannot be emitted unambiguously. GetFactoryTargets
        // drops all such controls; report it at the source declaration instead of failing silently.
        foreach (var group in models
                     .Where(static model => model.GenerateFactory &&
                                            model.IsConstructible &&
                                            model.Type.IsOrInheritsFrom("Avalonia.Controls.Control"))
                     .GroupBy(static model => SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name,
                         StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            // Keep a stable declaration id rather than a Location: the cached result can be
            // reported by another compilation whose syntax trees are different objects.
            var declaration = group
                .Select(static model => model.Type)
                .FirstOrDefault(static type => type.Locations.Any(static location => location.IsInSource));
            var locationMarker = declaration is null
                ? null
                : DocumentationCommentId.CreateDeclarationId(declaration);
            diagnostics.Add(new GenerationDiagnostic(
                "SVCUIGEN009",
                string.Format(CollidingFactoryName.MessageFormat.ToString(), group.Key, group.Count()),
                DiagnosticSeverity.Warning,
                locationMarker));
        }

        var host = SharedHost;
        foreach (var model in models)
        {
            var typeKey = TypeKey.Create(model.Type);
            var keyText = typeKey.ToString();
            if (!extensionKeys.Contains(keyText))
            {
                continue;
            }

            try
            {
                var publish = publishing.IsPublisher &&
                    (publishing.RestrictedRoots is null || publishing.RestrictedRoots.Contains(typeKey)) &&
                    ExternalGeneratorHost.CanPublish(model.Type);

                // Declarative views already get public value/binding-expression and event setters
                // from the dedicated generators. Suppress just those overloads; the BindingBase and
                // Style<T> setters below are not emitted anywhere else and must remain available.
                var suppressOverlappingSetters = MarkupTypeHelpers.IsAvaloniaMarkupView(model.Type);
                var code = host.GenerateExtensions(
                    model, compilation, publish, suppressOverlappingSetters, referenceHash);
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                if (publish)
                {
                    publishedKeys[keyText] = publishing.AssemblyName;
                }

                var typeName = model.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                var prefix = SymbolEqualityComparer.Default.Equals(model.Type.ContainingAssembly, compilation.Assembly)
                    ? "Local_"
                    : "External_";
                var hintName = $"{prefix}{SymbolUtilities.RemoveIllegalFileNameCharacters(typeName)}.g.cs";
                sources.Add(new GeneratedSource(hintName, code!));
            }
            catch (Exception exception)
            {
                AddFailure(diagnostics, model.Type, exception);
            }
        }

        var factoryTargets = GetFactoryTargets(models);
        GenerateFactoryUsings(sources, compilation);
        GenerateFactories(sources, compilation, factoryTargets);
        GenerateRegistrations(sources, GetRegistrationTargets(models));

        if (publishing.IsPublisher)
        {
            sources.Add(new GeneratedSource(
                "PublicMarkupExtensionControls.g.cs",
                ExtensionPublishing.CreateCoverageSource(publishedKeys)));
        }

        return new GenerationResult(sources.ToImmutableArray(), diagnostics.ToImmutableArray());
    }

    /// <summary>
    /// Emits the global static factory import only when the consumer explicitly opts in with
    /// <c>[assembly: GenerateGlobalControlFactories]</c>. No type aliases are generated: a generated
    /// static import can shadow type names, and patching that with more generated aliases breaks
    /// tooling that does not reproduce the generated compilation. Consumers should prefer a
    /// per-file <c>using static</c> or a hand-written project-level <c>Using Static</c> item.
    /// </summary>
    private static void GenerateFactoryUsings(List<GeneratedSource> sources, Compilation compilation)
    {
        if (!compilation.Assembly.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == GlobalFactoriesAttributeName))
        {
            return;
        }

        var source = new StringBuilder(128);
        source.AppendLine("// <auto-generated />");
        source.AppendLine("global using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;");
        sources.Add(new GeneratedSource("SVCSystemsAvaloniaFactoryUsings.g.cs", source.ToString()));
    }

    private static void GenerateFactories(
        List<GeneratedSource> sources,
        Compilation compilation,
        IReadOnlyList<ControlModel> factories)
    {
        var source = new StringBuilder(4096);
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#pragma warning disable CS1591");
        source.AppendLine("#nullable enable");
        source.AppendLine("namespace SVCSystems.Avalonia.CSharpUI;");
        source.AppendLine("/// <summary>");
        source.AppendLine("/// Creates controls used by this application and records their construction site.");
        source.AppendLine("/// </summary>");
        source.AppendLine("internal static class GeneratedControlFactories");
        source.AppendLine("{");

        foreach (var model in factories)
        {
            source.Append(FactoryMethodCache.GetValue(model.Type, _ =>
                new TextBox { Text = BuildFactoryMethod(model, compilation) }).Text);
        }

        source.AppendLine("}");
        sources.Add(new GeneratedSource("SVCSystemsCSharpUIControlFactories.g.cs", source.ToString()));
    }

    private static string BuildFactoryMethod(ControlModel model, Compilation compilation)
    {
        var methodName = SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name;
        var typeName = model.Type.GetFullTypeName();
        var source = new StringBuilder(512);
        var controlDoc = SymbolUtilities.FormatXmlDoc(model.Type, compilation);
        if (!string.IsNullOrWhiteSpace(controlDoc))
        {
            foreach (var line in controlDoc.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    source.AppendLine("    " + trimmed);
                }
            }
        }
        else
        {
            source.AppendLine($"    /// <summary>Creates a new <see cref=\"{SymbolUtilities.GetCrefName(model.Type)}\"/>.</summary>");
        }

        source.AppendLine($"    public static {typeName} {methodName}([global::System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null, [global::System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0)");
        source.AppendLine("    {");
        source.AppendLine($"        var control = new {typeName}();");
        source.AppendLine("        global::SVCSystems.Avalonia.CSharpUI.SourceInfo.Set(control, sourceFile, sourceLine);");
        source.AppendLine("        return control;");
        source.AppendLine("    }");
        return source.ToString();
    }

    private static void GenerateRegistrations(List<GeneratedSource> sources, IReadOnlyList<ControlModel> models)
    {
        foreach (var model in models)
        {
            var typeIdentifier = SymbolUtilities.BuildTypeIdentifier(model.Type);
            var registrationClassName = $"{typeIdentifier}_ViewFactoryRegistration";
            var hintName = SymbolUtilities.RemoveIllegalFileNameCharacters(registrationClassName) + ".g.cs";
            var code = RegistrationCache.GetValue(model.Type, _ =>
                new TextBox { Text = BuildRegistration(registrationClassName, model.Type) }).Text;

            sources.Add(new GeneratedSource(hintName, code));
        }
    }

    private static string BuildRegistration(string registrationClassName, INamedTypeSymbol type)
    {
        var sb = new StringBuilder(256);
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("namespace SVCSystems.Avalonia.CSharpUI;");
        sb.AppendLine($"internal static class {registrationClassName}");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        var typeName = type.GetFullTypeName();
        sb.AppendLine($"        global::SVCSystems.Avalonia.CSharpUI.ViewFactoryRegistry.Register<{typeName}>(static () => new {typeName}());");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static bool IsRegistrationEligible(ControlModel model) =>
        model.GenerateFactory &&
        model.IsConstructible &&
        model.Type.IsOrInheritsFrom("Avalonia.Controls.Control");

    private static ControlModel[] GetRegistrationTargets(IReadOnlyList<ControlModel> models) =>
        [.. models
            .Where(static model => IsRegistrationEligible(model))
            .OrderBy(static model => model.Type.ToDisplayString(), StringComparer.Ordinal)];

    private static ControlModel[] GetFactoryTargets(IReadOnlyList<ControlModel> models) =>
        [.. models
            .Where(static model => IsRegistrationEligible(model))
            .GroupBy(static model => SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.First())
            .OrderBy(static model => model.Type.ToDisplayString(), StringComparer.Ordinal)];

    private static void AddFailure(List<GenerationDiagnostic> diagnostics, INamedTypeSymbol symbol, Exception exception) =>
        diagnostics.Add(new GenerationDiagnostic(
            "SVCUIGEN003",
            string.Format(
                ExtensionGenerationFailed.MessageFormat.ToString(),
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                exception.Message),
            DiagnosticSeverity.Error));
}
