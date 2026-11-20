using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Analysis;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.Usage;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.InternalGenerators;

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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var names = UsageNameProvider.Create(context).Collect();
        var input = names.Combine(context.CompilationProvider);
        context.RegisterSourceOutput(input, static (sourceContext, pair) =>
            Generate(sourceContext, pair.Left, pair.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<string> names,
        Compilation compilation)
    {
        var catalog = ControlCatalog.Build(compilation);
        var usage = UsageResolver.Resolve(catalog, names);

        foreach (var ambiguousName in usage.AmbiguousNames)
        {
            context.ReportDiagnostic(Diagnostic.Create(AmbiguousControlName, Location.None, ambiguousName));
        }

        var cache = new Dictionary<INamedTypeSymbol, ControlModel>(SymbolEqualityComparer.Default);
        var models = new List<ControlModel>();
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
                ReportFailure(context, symbol, exception);
                continue;
            }

            if (!model.IgnoreGeneratedExtensions)
            {
                models.Add(model);
            }
        }

        var host = new ExternalGeneratorHost();
        foreach (var model in models)
        {
            try
            {
                var code = host.GenerateExtensions(model, compilation);
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                var typeName = model.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                var prefix = SymbolEqualityComparer.Default.Equals(model.Type.ContainingAssembly, compilation.Assembly)
                    ? "Local_"
                    : "External_";
                var hintName = $"{prefix}{SymbolUtilities.RemoveIllegalFileNameCharacters(typeName)}.g.cs";
                context.AddSource(hintName, SourceText.From(code!, Encoding.UTF8));
            }
            catch (Exception exception)
            {
                ReportFailure(context, model.Type, exception);
            }
        }

        var factoryTargets = GetFactoryTargets(models);
        GenerateFactoryUsings(context, compilation, factoryTargets);
        GenerateFactories(context, compilation, factoryTargets);
        GenerateRegistrations(context, factoryTargets);
    }

    private static void GenerateFactoryUsings(
        SourceProductionContext context,
        Compilation compilation,
        IReadOnlyList<ControlModel> factories)
    {
        var factoryNames = new HashSet<string>(
            factories.Select(static model => SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name),
            StringComparer.Ordinal);
        var resolution = TypeAliasResolver.Resolve(compilation, factoryNames);

        foreach (var skipped in resolution.Skipped)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnaliasableFactoryName,
                Location.None,
                skipped.Name,
                skipped.Reason));
        }

        var source = new StringBuilder(512);
        source.AppendLine("// <auto-generated />");
        source.AppendLine("global using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;");
        foreach (var alias in resolution.Aliases)
        {
            source.AppendLine($"global using {alias.Key} = {alias.Value};");
        }

        context.AddSource("SVCSystemsAvaloniaFactoryUsings.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void GenerateFactories(
        SourceProductionContext context,
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
        context.AddSource("SVCSystemsCSharpUIControlFactories.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void GenerateRegistrations(SourceProductionContext context, IReadOnlyList<ControlModel> factories)
    {
        foreach (var model in factories)
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

            context.AddSource(hintName, SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    private static ControlModel[] GetFactoryTargets(IReadOnlyList<ControlModel> models) =>
        [.. models
            .Where(static model => model.GenerateFactory &&
                model.IsConstructible &&
                model.Type.IsOrInheritsFrom("Avalonia.Controls.Control"))
            .GroupBy(static model => SymbolUtilities.CleanIdentifier(model.FactoryName) ?? model.Type.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.First())
            .OrderBy(static model => model.Type.ToDisplayString(), StringComparer.Ordinal)];

    private static void ReportFailure(SourceProductionContext context, INamedTypeSymbol symbol, Exception exception) =>
        context.ReportDiagnostic(Diagnostic.Create(
            ExtensionGenerationFailed,
            symbol.Locations.FirstOrDefault(),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            exception.Message));
}
