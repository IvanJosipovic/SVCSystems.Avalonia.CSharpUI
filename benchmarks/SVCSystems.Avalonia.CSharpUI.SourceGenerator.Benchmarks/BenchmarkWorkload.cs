using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.Benchmarks;

/// <summary>
/// Builds a deterministic, medium-sized compilation that exercises every part of the generator:
/// many local views, many simple names, all curated common controls and a XAML resource request.
/// </summary>
internal static class BenchmarkWorkload
{
    internal static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const int ViewCount = 40;
    private const string EditableFileName = "EditableView.cs";

    private static readonly string[] Controls =
    [
        "Button", "TextBlock", "TextBox", "StackPanel", "Grid", "Border", "Image",
        "CheckBox", "ComboBox", "ListBox", "Slider", "ProgressBar", "TabControl",
        "Menu", "ToolTip", "Expander", "ScrollViewer", "UniformGrid", "WrapPanel",
        "Canvas", "DockPanel", "Separator", "ToggleButton", "RadioButton"
    ];

    internal static ImmutableArray<MetadataReference> References { get; } = CreateReferences();

    /// <summary>The unmodified compilation (baseline for cold and incremental benchmarks).</summary>
    internal static Compilation Base { get; } = CreateCompilation(editableSuffix: string.Empty);

    /// <summary>Same compilation with an edit that does not change any simple name.</summary>
    internal static Compilation IrrelevantEdit { get; } =
        ReplaceEditableTree(Base, "// a comment that changes no simple name");

    /// <summary>Same compilation with an edit that introduces a new simple name.</summary>
    internal static Compilation NameEdit { get; } =
        ReplaceEditableTree(Base, "var _ = new CalendarDatePicker();");

    /// <summary>
    /// Distinct compilations for cold/real-edit iterations. The generator's static caches hold only
    /// a handful of results, so cycling through these forces genuine regeneration each iteration
    /// instead of accidentally benchmarking a cache hit.
    /// </summary>
    internal static Compilation[] ColdVariants { get; } =
        Enumerable.Range(0, 8)
            .Select(index => CreateCompilation(editableSuffix: $"int cold{index} = {index};"))
            .ToArray();

    private static readonly string[] NameVariantControls =
    [
        "CalendarDatePicker", "DatePicker", "TimePicker", "ColorPicker",
        "NavigationView", "SplitView", "InfoBar", "BreadcrumbBar"
    ];

    internal static Compilation[] NameVariants { get; } =
        Enumerable.Range(0, 8)
            .Select(index => ReplaceEditableTree(
                Base, $"var _ = new {NameVariantControls[index % NameVariantControls.Length]}();"))
            .ToArray();

    /// <summary>Distinct XAML compilations so the static generation cache cannot be hit.</summary>
    internal static Compilation[] XamlVariants { get; } =
        Enumerable.Range(0, 8).Select(CreateXamlCompilation).ToArray();

    /// <summary>Compilation that exercises XamlResourceGenerator with a real compiled-XAML factory.</summary>
    internal static Compilation Xaml { get; } = CreateXamlCompilation();

    private static Compilation ReplaceEditableTree(Compilation compilation, string editableSuffix)
    {
        var oldTree = compilation.SyntaxTrees.First(static tree => tree.FilePath == EditableFileName);
        return compilation.ReplaceSyntaxTree(oldTree, CreateEditableTree(editableSuffix));
    }

    internal static string EditableTreePath => EditableFileName;

    private static Compilation CreateXamlCompilation(int index = 0)
    {
        const string prefix = "avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/";
        var source =
            $"[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource(\"{prefix}Resources.axaml\", \"GeneratedResources{index}\")]\n" +
            "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v10.0\")]\n";
        return CSharpCompilation.Create("XamlResourceBenchmark",
            [CSharpSyntaxTree.ParseText(source, ParseOptions, path: "XamlRequest.cs")],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static SyntaxTree CreateEditableTree(string editableSuffix)
    {
        var body = new System.Text.StringBuilder(1024);
        for (var c = 0; c < Controls.Length; c++)
        {
            body.Append("        _ = new ").Append(Controls[c]).Append("();\n");
        }

        body.Append("        _ = new System.Collections.Generic.List<").Append(Controls[0]).Append(">();\n");
        body.Append("        object value0 = null;\n");
        body.Append("        System.Console.WriteLine(value0);\n");
        if (editableSuffix.Length > 0)
        {
            body.Append("        ").AppendLine(editableSuffix);
        }

        var source = new System.Text.StringBuilder(1400)
            .AppendLine("using Avalonia.Controls;")
            .AppendLine("using System;")
            .AppendLine("namespace Bench.Views;")
            .AppendLine()
            .Append("public sealed class BenchmarkView0")
            .AppendLine(" : SVCSystems.Avalonia.CSharpUI.ViewBase<object>")
            .AppendLine("{")
            .Append("    public BenchmarkView0() : base(new object()) { }")
            .AppendLine()
            .AppendLine("    protected override object Build(object vm)")
            .AppendLine("    {")
            .Append(body)
            .AppendLine("        return new StackPanel();")
            .AppendLine("    }")
            .AppendLine("}")
            .ToString();

        return CSharpSyntaxTree.ParseText(source, ParseOptions, path: EditableFileName);
    }

    private static Compilation CreateCompilation(string editableSuffix)
    {
        var trees = new List<SyntaxTree>(ViewCount + 2)
        {
            CreateEditableTree(editableSuffix)
        };

        for (var i = 1; i < ViewCount; i++)
        {
            // Mix factory-style calls (simple names) and object creation so both syntax shapes
            // are present. Include inheritance and generic names to widen the syntax surface.
            var body = new System.Text.StringBuilder(1024);
            for (var c = 0; c < Controls.Length; c++)
            {
                body.Append("        _ = new ").Append(Controls[c]).Append("();\n");
            }

            body.Append("        _ = new System.Collections.Generic.List<").Append(Controls[i % Controls.Length]).Append(">();\n");
            body.Append("        object value").Append(i).Append(" = null;\n");
            body.Append("        System.Console.WriteLine(value").Append(i).Append(");\n");

            var source = new System.Text.StringBuilder(1400)
                .AppendLine("using Avalonia.Controls;")
                .AppendLine("using System;")
                .AppendLine("namespace Bench.Views;")
                .AppendLine()
                .Append("public sealed class BenchmarkView").Append(i)
                .AppendLine(" : SVCSystems.Avalonia.CSharpUI.ViewBase<object>")
                .AppendLine("{")
                .Append("    public BenchmarkView").Append(i).AppendLine("() : base(new object()) { }")
                .AppendLine()
                .AppendLine("    protected override object Build(object vm)")
                .AppendLine("    {")
                .Append(body)
                .AppendLine("        return new StackPanel();")
                .AppendLine("    }")
                .AppendLine("}")
                .ToString();

            trees.Add(CSharpSyntaxTree.ParseText(source, ParseOptions, path: $"View{i}.cs"));
        }

        trees.Add(CSharpSyntaxTree.ParseText(
            "[assembly: SVCSystems.Avalonia.CSharpUI.GenerateMarkupControl(typeof(Avalonia.Controls.Button), typeof(Avalonia.Controls.TextBlock))]\n" +
            "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v10.0\")]\n",
            ParseOptions, path: "AssemblyAttributes.cs"));

        return CSharpCompilation.Create("GeneratorBenchmarkCompilation",
            trees, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted is not null)
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }
        }

        // Force-load the assemblies whose types the generator looks up.
        _ = typeof(global::Avalonia.AvaloniaObject).Assembly;
        _ = typeof(global::Avalonia.Controls.Button).Assembly;
        _ = typeof(global::Avalonia.Styling.Style).Assembly;
        _ = typeof(global::Avalonia.Media.Brush).Assembly;
        _ = typeof(global::Avalonia.Input.InputElement).Assembly;
        _ = typeof(SVCSystems.Avalonia.CSharpUI.ViewBase).Assembly;

        // Include every managed assembly next to the benchmark binary. This mirrors a real
        // application's reference set (Avalonia.Base, Avalonia.Controls, Avalonia.Markup, ...).
        var outputDirectory = Path.GetDirectoryName(typeof(BenchmarkWorkload).Assembly.Location);
        if (outputDirectory is not null)
        {
            foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.dll"))
            {
                if (IsManagedAssembly(path))
                {
                    paths.Add(path);
                }
            }
        }

        return [.. paths.Where(File.Exists).Select(static path => MetadataReference.CreateFromFile(path))];
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileLoadException)
        {
            return false;
        }
    }
}
