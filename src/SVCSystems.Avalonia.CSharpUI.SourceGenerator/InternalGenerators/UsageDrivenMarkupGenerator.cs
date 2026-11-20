using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

internal readonly struct GeneratedSource(string hintName, string text) : IEquatable<GeneratedSource>
{
    internal string HintName { get; } = hintName;

    internal string Text { get; } = text;

    public bool Equals(GeneratedSource other) =>
        string.Equals(HintName, other.HintName, StringComparison.Ordinal) &&
        string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GeneratedSource other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((HintName?.GetHashCode() ?? 0) * 397) ^ (Text?.GetHashCode() ?? 0);
        }
    }
}

internal readonly struct GenerationDiagnostic(string id, string message, DiagnosticSeverity severity) : IEquatable<GenerationDiagnostic>
{
    internal string Id { get; } = id;

    internal string Message { get; } = message;

    internal DiagnosticSeverity Severity { get; } = severity;

    public bool Equals(GenerationDiagnostic other) =>
        string.Equals(Id, other.Id, StringComparison.Ordinal) &&
        string.Equals(Message, other.Message, StringComparison.Ordinal) &&
        Severity == other.Severity;

    public override bool Equals(object? obj) => obj is GenerationDiagnostic other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (Id?.GetHashCode() ?? 0) * 397 ^ (Message?.GetHashCode() ?? 0);
            return (hash * 397) ^ (int)Severity;
        }
    }
}

/// <summary>
/// The complete generated output for one compilation. Equality is based on the generated text, so
/// Roslyn caches the registered source output across edits that don't change the generated APIs.
/// </summary>
internal sealed class GenerationResult(
    ImmutableArray<GeneratedSource> sources,
    ImmutableArray<GenerationDiagnostic> diagnostics)
{
    internal ImmutableArray<GeneratedSource> Sources { get; } = sources;

    internal ImmutableArray<GenerationDiagnostic> Diagnostics { get; } = diagnostics;
}

internal sealed class GenerationResultComparer : IEqualityComparer<GenerationResult>
{
    internal static readonly GenerationResultComparer Instance = new();

    public bool Equals(GenerationResult? x, GenerationResult? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null &&
         x.Sources.SequenceEqual(y.Sources) &&
         x.Diagnostics.SequenceEqual(y.Diagnostics));

    public int GetHashCode(GenerationResult result)
    {
        unchecked
        {
            var hash = 17;
            foreach (var source in result.Sources)
            {
                hash = (hash * 31) + source.GetHashCode();
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                hash = (hash * 31) + diagnostic.GetHashCode();
            }

            return hash;
        }
    }
}

/// <summary>
/// Generates fluent extensions, control factories and factory registrations for the controls
/// that the application actually mentions in source (plus their base-type closure).
/// </summary>
public sealed class UsageDrivenMarkupGenerator
{
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

    internal static readonly DiagnosticDescriptor UnaliasableFactoryName = new(
        id: "SVCUIGEN007",
        title: "Control factory name cannot be aliased",
        messageFormat: "Control factory name '{0}' cannot be aliased because it {1}. Usages of that name as a type may be shadowed by the generated factory call, so qualify them with their namespace.",
        category: "SVCSystems.Avalonia.CSharpUI",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A factory name is also used as a type, but a global alias cannot be generated for it. Qualify type usages with their namespace.");

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

        // Produce an equatable result first. Registering the output on that result (instead of on
        // the compilation) lets Roslyn cache it when an unrelated edit leaves generated text and
        // diagnostics unchanged, instead of re-adding every file on each keystroke.
        var generation = input
            .Select(static (pair, cancellationToken) => Generate(pair.Left, pair.Right, cancellationToken))
            .WithComparer(GenerationResultComparer.Instance);

        context.RegisterSourceOutput(generation, static (sourceContext, result) =>
        {
            foreach (var source in result.Sources)
            {
                sourceContext.AddSource(source.HintName, SourceText.From(source.Text, Encoding.UTF8));
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                sourceContext.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        diagnostic.Id,
                        diagnostic.Id,
                        diagnostic.Message,
                        "SVCSystems.Avalonia.CSharpUI",
                        diagnostic.Severity,
                        isEnabledByDefault: true),
                    Location.None));
            }
        });
    }

    private static GenerationResult Generate(
        ImmutableArray<string> names,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sources = new List<GeneratedSource>();
        var diagnostics = new List<GenerationDiagnostic>();

        var catalog = ControlCatalog.Build(compilation);
        var usage = UsageResolver.Resolve(catalog, names);
        var publishing = ExtensionPublishing.Read(compilation);

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
            if (publishing.ReferencedCoverage.Contains(key.ToString()))
            {
                continue;
            }

            extensionKeys.Add(key.ToString());
        }

        foreach (var ambiguousName in usage.AmbiguousNames)
        {
            diagnostics.Add(new GenerationDiagnostic(
                "SVCUIGEN006",
                string.Format(AmbiguousControlName.MessageFormat.ToString(), ambiguousName),
                DiagnosticSeverity.Info));
        }

        var cache = new Dictionary<INamedTypeSymbol, ControlModel>(SymbolEqualityComparer.Default);
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
                model = ControlModelBuilder.Build(symbol, compilation.Assembly, cache);
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

        var host = new ExternalGeneratorHost();
        foreach (var model in models)
        {
            if (!extensionKeys.Contains(TypeKey.Create(model.Type).ToString()))
            {
                continue;
            }

            try
            {
                var typeKey = TypeKey.Create(model.Type);
                var publish = publishing.IsPublisher &&
                    (publishing.RestrictedRoots is null || publishing.RestrictedRoots.Contains(typeKey)) &&
                    ExternalGeneratorHost.CanPublish(model.Type);

                // Declarative views already get public value/binding-expression and event setters
                // from the dedicated generators. Suppress just those overloads; the BindingBase and
                // Style<T> setters below are not emitted anywhere else and must remain available.
                var suppressOverlappingSetters = MarkupTypeHelpers.IsAvaloniaMarkupView(model.Type);
                var code = host.GenerateExtensions(model, compilation, publish, suppressOverlappingSetters);
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                if (publish)
                {
                    publishedKeys[typeKey.ToString()] = publishing.AssemblyName;
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
        GenerateFactoryUsings(sources, diagnostics, compilation, factoryTargets);
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

    private static void GenerateFactoryUsings(
        List<GeneratedSource> sources,
        List<GenerationDiagnostic> diagnostics,
        Compilation compilation,
        IReadOnlyList<ControlModel> factories)
    {
        var factoryNames = new HashSet<string>(
            factories.Select(static model => SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name),
            StringComparer.Ordinal);
        var resolution = TypeAliasResolver.Resolve(compilation, factoryNames);

        foreach (var skipped in resolution.Skipped)
        {
            diagnostics.Add(new GenerationDiagnostic(
                "SVCUIGEN007",
                string.Format(UnaliasableFactoryName.MessageFormat.ToString(), skipped.Name, skipped.Reason),
                DiagnosticSeverity.Warning));
        }

        var source = new StringBuilder(512);
        source.AppendLine("// <auto-generated />");
        source.AppendLine("global using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;");
        foreach (var alias in resolution.Aliases)
        {
            source.AppendLine($"global using {alias.Key} = {alias.Value};");
        }

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
            var methodName = SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name;
            var typeName = model.Type.GetFullTypeName();
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
                source.AppendLine($"    /// <summary>Creates a new <see cref=\"{typeName}\"/>.</summary>");
            }
            source.AppendLine($"    public static {typeName} {methodName}([global::System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null, [global::System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0)");
            source.AppendLine("    {");
            source.AppendLine($"        var control = new {typeName}();");
            source.AppendLine("        global::SVCSystems.Avalonia.CSharpUI.SourceInfo.Set(control, sourceFile, sourceLine);");
            source.AppendLine("        return control;");
            source.AppendLine("    }");
        }

        source.AppendLine("}");
        sources.Add(new GeneratedSource("SVCSystemsCSharpUIControlFactories.g.cs", source.ToString()));
    }

    private static void GenerateRegistrations(List<GeneratedSource> sources, IReadOnlyList<ControlModel> models)
    {
        foreach (var model in models)
        {
            var typeIdentifier = SymbolUtilities.BuildTypeIdentifier(model.Type);
            var registrationClassName = $"{typeIdentifier}_ViewFactoryRegistration";
            var hintName = SymbolUtilities.RemoveIllegalFileNameCharacters(registrationClassName) + ".g.cs";

            var sb = new StringBuilder(256);
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("namespace SVCSystems.Avalonia.CSharpUI;");
            sb.AppendLine($"internal static class {registrationClassName}");
            sb.AppendLine("{");
            sb.AppendLine("    [ModuleInitializer]");
            sb.AppendLine("    internal static void Register()");
            sb.AppendLine("    {");
            var typeName = model.Type.GetFullTypeName();
            sb.AppendLine($"        global::SVCSystems.Avalonia.CSharpUI.ViewFactoryRegistry.Register<{typeName}>(static () => new {typeName}());");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            sources.Add(new GeneratedSource(hintName, sb.ToString()));
        }
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
