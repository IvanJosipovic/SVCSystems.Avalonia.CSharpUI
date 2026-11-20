using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using SVCSystems.Avalonia.CSharpUI;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.InteropServices;
using System.Reflection;

namespace SVCSystems.Avalonia.CSharpUI.Tests.SourceGeneratorTests;

public class MarkupExtensionGenerationTests
{
    private const string FixtureNamespace = "MarkupExtensionGeneratorFixture";
    private static readonly ImmutableArray<MetadataReference> PlatformReferences = CreatePlatformReferences();

    // Expected sources are from Avalonia.Markup.Declarative commit
    // 619df233a648cd7cddb40de9d0e437ef9b452832, normalized for CSharpUI's namespace and generator identity.
    [Fact]
    public void Compiled_fixture_extensions_match_the_pinned_amd_output()
    {
        var testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
        var expectedFiles = Directory.GetFiles(testDataPath, "*Extensions.cs");
        Assert.NotEmpty(expectedFiles);

        // Run the generator in memory instead of reading EmitCompilerGeneratedFiles from disk:
        // that path depends on the build configuration and can contain stale files on CI.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var implicitUsings = CSharpSyntaxTree.ParseText(
            "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using Avalonia;\nglobal using Avalonia.Controls;\nglobal using SVCSystems.Avalonia.CSharpUI;\n",
            parseOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        var fixtureTrees = Directory.GetFiles(testDataPath, "*.cs")
            .Where(static path => !Path.GetFileName(path).EndsWith("Extensions.cs", StringComparison.Ordinal))
            .Select(path => CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                parseOptions,
                cancellationToken: TestContext.Current.CancellationToken))
            .Prepend(implicitUsings);
        var compilation = CSharpCompilation.Create(
            "FixtureControls",
            fixtureTrees,
            PlatformReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var result = RunGenerator(compilation);
        AssertNoErrors(result.OutputCompilation);

        foreach (var expectedFile in expectedFiles)
        {
            var fixtureName = Path.GetFileNameWithoutExtension(expectedFile);
            Assert.EndsWith("Extensions", fixtureName, StringComparison.Ordinal);
            var controlName = fixtureName[..^"Extensions".Length];
            var generated = Assert.Single(result.GeneratedSources, source =>
                source.Contains($"internal static partial class Tests_{controlName}_MarkupExtensions", StringComparison.Ordinal));

            // Documentation comments depend on whether the host compilation retains doc comments;
            // they have their own dedicated test. Compare the generated API shape only.
            Assert.Equal(
                NormalizeIgnoringDocumentation(File.ReadAllText(expectedFile)),
                NormalizeIgnoringDocumentation(generated));
        }
    }

    [Fact]
    public void Used_control_generates_internal_extensions_and_unused_controls_do_not()
    {
        var result = RunGenerator(CreateCompilation("UsedControl", CreateSource(string.Empty, """
            public static class Usage
            {
                public static Button SetWidth() => new Button().Width(120);
            }
            """)));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("internal static partial class Avalonia_Controls_Button_MarkupExtensions", StringComparison.Ordinal));
        Assert.DoesNotContain(result.GeneratedSources, source =>
            source.Contains("Avalonia_Controls_DatePicker_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Common_controls_are_generated_without_being_mentioned_in_source()
    {
        var result = RunGenerator(CreateCompilation("CommonControls", "using Avalonia.Controls;"));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("internal static partial class Avalonia_Controls_TextBlock_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("internal static partial class Avalonia_Controls_StackPanel_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains("internal static partial class Avalonia_Layout_Layoutable_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_control_declaration_is_a_usage_root()
    {
        var result = RunGenerator(CreateCompilation("LocalControlRoot", CreateSource(string.Empty, string.Empty)));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains($"internal static partial class {FixtureNamespace}_FixtureControl_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Factory_call_name_is_detected_without_an_explicit_type_reference()
    {
        const string source = """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;
            using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;

            public static class Usage
            {
                public static Button Create() => Button();
            }
            """;
        var result = RunGenerator(CreateCompilation("FactoryCallUsage", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("public static global::Avalonia.Controls.Button Button(", StringComparison.Ordinal));
    }

    [Fact]
    public void Binding_valued_Avalonia_properties_generate_value_and_compiled_binding_setters()
    {
        var usage = """
            public static class Usage
            {
                public static void Mention()
                {
                    _ = typeof(AutoCompleteBox);
                    _ = typeof(ItemsControl);
                    _ = typeof(Avalonia.Controls.Primitives.SelectingItemsControl);
                    _ = typeof(TableViewColumn);
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("BindingValuedAvaloniaProperties", CreateSource(string.Empty, usage)));

        AssertNoErrors(result.OutputCompilation);

        var properties = new[]
        {
            (Control: "AutoCompleteBox", Property: "ValueMemberBinding"),
            (Control: "ItemsControl", Property: "DisplayMemberBinding"),
            (Control: "Primitives_SelectingItemsControl", Property: "SelectedValueBinding"),
            (Control: "TableViewColumn", Property: "Binding")
        };

        foreach (var (control, property) in properties)
        {
            var generated = Assert.Single(result.GeneratedSources, source =>
                source.Contains($"internal static partial class Avalonia_Controls_{control}_MarkupExtensions", StringComparison.Ordinal));

            Assert.Contains(
                $"public static T {property}<T>(this T control, global::Avalonia.Data.BindingBase? value",
                generated,
                StringComparison.Ordinal);
            Assert.Contains(
                $"public static T {property}<T, TViewModel, TValue>(this T control, TViewModel source, Expression<Func<TViewModel, TValue>> getter",
                generated,
                StringComparison.Ordinal);
            Assert.Contains(
                $"public static Style<T> {property}<T>(this Style<T> style, global::Avalonia.Data.BindingBase? value",
                generated,
                StringComparison.Ordinal);
            Assert.Contains(
                $"public static Style<T> {property}<T, TViewModel, TValue>(this Style<T> style, TViewModel source, Expression<Func<TViewModel, TValue>> getter",
                generated,
                StringComparison.Ordinal);
            Assert.Contains(
                $"public static Style<T> {property}<T, TViewModel, TValue>(this Style<T> style, Expression<Func<TViewModel, TValue>> getter",
                generated,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Added_owner_property_generates_a_derived_control_extension()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            namespace InheritanceFixture
            {
                public class BaseControl : Control
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<BaseControl, int>(nameof(Value));

                    public virtual int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }

                public sealed class AddedOwnerControl : BaseControl
                {
                    public new static readonly StyledProperty<int> ValueProperty =
                        BaseControl.ValueProperty.AddOwner<AddedOwnerControl>();

                    public new int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }

                public static class Usage
                {
                    public static AddedOwnerControl SetValue(AddedOwnerControl control) => control.Value(42);
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("AddedOwnerControlExtensions", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("public static T Value<T>", StringComparison.Ordinal) &&
            generated.Contains("where T : global::InheritanceFixture.BaseControl", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains(
                "Value(this global::InheritanceFixture.AddedOwnerControl control, global::System.Int32 value",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Generated_factories_skip_inaccessible_external_constructors()
    {
        var controls = CreateCompilation("ExternalFactoryControls", """
            using Avalonia.Controls;

            namespace ExternalFactoryFixture;

            public class InternalConstructorControl : Control
            {
                internal InternalConstructorControl() { }
            }

            public class PublicConstructorControl : Control
            {
                public PublicConstructorControl() { }
            }
            """);
        var controlsReference = EmitReference(controls);
        var source = """
            using ExternalFactoryFixture;

            public static class Usage
            {
                public static void Mention()
                {
                    _ = typeof(PublicConstructorControl);
                    _ = typeof(InternalConstructorControl);
                }
            }
            """;
        var compilation = CreateCompilationWithReferences("ExternalFactoryConsumer", source, controlsReference);
        var result = RunGenerator(compilation);

        AssertNoErrors(result.OutputCompilation);
        var factorySource = Assert.Single(result.GeneratedSources, static sourceText =>
            sourceText.Contains("internal static class GeneratedControlFactories", StringComparison.Ordinal));
        Assert.Contains("PublicConstructorControl", factorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("InternalConstructorControl(", factorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_alias_changes_the_detected_factory_name()
    {
        const string source = """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            [SVCSystemsCSharpUIGeneratorMetadata(Alias = "Badge")]
            public sealed class LevelBadge : ContentControl { }

            public static class Usage
            {
                public static LevelBadge Create() => new LevelBadge();
            }
            """;
        var result = RunGenerator(CreateCompilation("AliasFactory", source));

        AssertNoErrors(result.OutputCompilation);
        var factorySource = Assert.Single(result.GeneratedSources, static sourceText =>
            sourceText.Contains("internal static class GeneratedControlFactories", StringComparison.Ordinal));
        Assert.Contains("public static global::LevelBadge Badge([global::", factorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("public static global::LevelBadge LevelBadge([global::", factorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Ambiguous_factory_name_reports_SVCUIGEN006()
    {
        static PortableExecutableReference CreateProvider(string assemblyName, string @namespace) =>
            EmitReference(CreateCompilation(assemblyName, $$"""
                using Avalonia;
                using Avalonia.Controls;
                using SVCSystems.Avalonia.CSharpUI;

                namespace {{@namespace}}
                {
                    public sealed class SharedName : Control
                    {
                        public static readonly StyledProperty<int> ValueProperty =
                            AvaloniaProperty.Register<SharedName, int>(nameof(Value));

                        public int Value
                        {
                            get => GetValue(ValueProperty);
                            set => SetValue(ValueProperty, value);
                        }
                    }
                }
                """));

        var first = CreateProvider("AmbiguousProviderOne", "AmbiguousOne");
        var second = CreateProvider("AmbiguousProviderTwo", "AmbiguousTwo");
        var consumer = RunGenerator(CreateCompilationWithReferences(
            "AmbiguousConsumer",
            """
            public static class Usage
            {
                public static void Mention() => _ = new AmbiguousOne.SharedName();
            }
            """,
            first,
            second));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.Contains(consumer.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN006");
        Assert.Contains(consumer.GeneratedSources, static source =>
            source.Contains("AmbiguousOne_SharedName_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains(consumer.GeneratedSources, static source =>
            source.Contains("AmbiguousTwo_SharedName_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Generated_extensions_copy_xml_documentation_from_metadata_and_source()
    {
        var result = RunGenerator(CreateCompilation("GeneratedDocumentation", CreateSource(string.Empty, """
            public static class Usage
            {
                public static Button SetWidth() => new Button().Width(120);
            }
            """)));

        AssertNoErrors(result.OutputCompilation);

        // Avalonia's Button.ClickMode property is documented in the assembly's XML file.
        var buttonExtensions = Assert.Single(result.GeneratedSources, static source =>
            source.Contains("internal static partial class Avalonia_Controls_Button_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains("/// <summary>", buttonExtensions, StringComparison.Ordinal);
        Assert.Contains("should react to clicks", buttonExtensions, StringComparison.Ordinal);

        // The local fixture property is documented in source.
        var fixtureExtensions = Assert.Single(result.GeneratedSources, static source =>
            source.Contains($"internal static partial class {FixtureNamespace}_FixtureControl_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains("/// <summary>The fixture control value.</summary>", fixtureExtensions, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_metadata_suppresses_a_used_control()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            [SVCSystemsCSharpUIGeneratorMetadata(Ignore = true)]
            public sealed class IgnoredControl : Control
            {
                public static readonly StyledProperty<int> ValueProperty =
                    AvaloniaProperty.Register<IgnoredControl, int>(nameof(Value));

                public int Value
                {
                    get => GetValue(ValueProperty);
                    set => SetValue(ValueProperty, value);
                }
            }

            public static class Usage
            {
                public static IgnoredControl Create() => new IgnoredControl();
            }
            """;
        var result = RunGenerator(CreateCompilation("IgnoredControl", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.DoesNotContain(result.GeneratedSources, static source =>
            source.Contains("IgnoredControl_MarkupExtensions", StringComparison.Ordinal));
        var factories = Assert.Single(result.GeneratedSources, static source =>
            source.Contains("internal static class GeneratedControlFactories", StringComparison.Ordinal));
        Assert.DoesNotContain(" IgnoredControl(", factories, StringComparison.Ordinal);
    }

    [Fact]
    public void Factories_and_registrations_follow_the_generated_set()
    {
        var result = RunGenerator(CreateCompilation("FactoryScope", CreateSource(string.Empty, """
            public static class Usage
            {
                public static Button Create() => new Button();
            }
            """)));

        AssertNoErrors(result.OutputCompilation);
        var factories = Assert.Single(result.GeneratedSources, static source =>
            source.Contains("internal static class GeneratedControlFactories", StringComparison.Ordinal));
        Assert.Contains(" Button(", factories, StringComparison.Ordinal);
        Assert.DoesNotContain(" DatePicker(", factories, StringComparison.Ordinal);
        Assert.Contains(result.GeneratedSources, static source =>
            source.Contains($"{FixtureNamespace}_FixtureControl_ViewFactoryRegistration", StringComparison.Ordinal));
        Assert.DoesNotContain(result.GeneratedSources, static source =>
            source.Contains("DatePicker_ViewFactoryRegistration", StringComparison.Ordinal));
    }

    private static PortableExecutableReference CreateExternalWidgetLibrary() =>
        EmitReference(CreateCompilation("ExternalWidgetLibrary", """
            using Avalonia;
            using Avalonia.Controls;

            namespace ExternalWidgetFixture;

            public sealed class ExternalWidget : Control
            {
                public static readonly StyledProperty<int> ValueProperty =
                    AvaloniaProperty.Register<ExternalWidget, int>(nameof(Value));

                public int Value
                {
                    get => GetValue(ValueProperty);
                    set => SetValue(ValueProperty, value);
                }
            }
            """));

    [Fact]
    public void Custom_control_library_is_discovered_without_attributes()
    {
        var library = CreateExternalWidgetLibrary();
        var consumer = RunGenerator(CreateCompilationWithReferences("ExternalWidgetConsumer", """
            using ExternalWidgetFixture;
            using SVCSystems.Avalonia.CSharpUI;

            public static class Usage
            {
                public static ExternalWidget Create() => new ExternalWidget().Value(1);
            }
            """, library));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.Contains(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class ExternalWidgetFixture_ExternalWidget_MarkupExtensions", StringComparison.Ordinal));
    }

    private static PortableExecutableReference CreateAttachedHostLibrary(string alias = "") =>
        EmitReference(CreateCompilation("AttachedHostLibrary_" + alias, $$"""
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            namespace AttachedHostFixture;

            public sealed class HostControl : Control
            {
                {{alias}}public static readonly AttachedProperty<bool> DockProperty =
                    AvaloniaProperty.RegisterAttached<HostControl, Control, bool>("Dock");

                public static bool GetDock(AvaloniaObject element) => element.GetValue(DockProperty);
                public static void SetDock(AvaloniaObject element, bool value) => element.SetValue(DockProperty, value);
            }
            """));

    [Fact]
    public void Attached_property_extension_name_roots_its_host()
    {
        var library = CreateAttachedHostLibrary();
        var consumer = RunGenerator(CreateCompilationWithReferences("AttachedHostConsumer", """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            public static class Usage
            {
                public static TextBlock Apply() => new TextBlock().HostControl_Dock(true);
            }
            """, library));

        AssertNoErrors(consumer.OutputCompilation);
        var hostExtensions = Assert.Single(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class AttachedHostFixture_HostControl_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains("HostControl_Dock", hostExtensions, StringComparison.Ordinal);
    }

    [Fact]
    public void Aliased_attached_property_extension_name_roots_its_host()
    {
        var library = CreateAttachedHostLibrary("[SVCSystemsCSharpUIGeneratorMetadata(Alias = \"Pin\")]\n                ");
        var consumer = RunGenerator(CreateCompilationWithReferences("AliasedAttachedHostConsumer", """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            public static class Usage
            {
                public static TextBlock Apply() => new TextBlock().HostControl_Pin(true);
            }
            """, library));

        AssertNoErrors(consumer.OutputCompilation);
        var hostExtensions = Assert.Single(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class AttachedHostFixture_HostControl_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains("HostControl_Pin", hostExtensions, StringComparison.Ordinal);
    }

    [Fact]
    public void Attached_property_on_static_host_is_generated()
    {
        var library = EmitReference(CreateCompilation("StaticAttachedHostLibrary", """
            using Avalonia;
            using Avalonia.Controls;

            namespace StaticAttachedHostFixture;

            public sealed class StaticHost
            {
                public static readonly AttachedProperty<bool> PinProperty =
                    AvaloniaProperty.RegisterAttached<StaticHost, Control, bool>("Pin");

                public static bool GetPin(AvaloniaObject element) => element.GetValue(PinProperty);
                public static void SetPin(AvaloniaObject element, bool value) => element.SetValue(PinProperty, value);
            }
            """));
        var consumer = RunGenerator(CreateCompilationWithReferences("StaticAttachedHostConsumer", """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            public static class Usage
            {
                public static TextBlock Apply() => new TextBlock().StaticHost_Pin(true);
            }
            """, library));

        AssertNoErrors(consumer.OutputCompilation);
        var hostExtensions = Assert.Single(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class StaticAttachedHostFixture_StaticHost_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains("StaticHost_Pin", hostExtensions, StringComparison.Ordinal);
    }

    [Fact]
    public void Assembly_attribute_is_a_per_type_opt_in_for_unmentioned_controls()
    {
        var library = CreateExternalWidgetLibrary();
        var consumer = RunGenerator(CreateCompilationWithReferences("PerTypeOptIn", """
            using SVCSystems.Avalonia.CSharpUI;

            [assembly: GenerateMarkupControl(typeof(ExternalWidgetFixture.ExternalWidget))]
            """, library));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.Contains(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class ExternalWidgetFixture_ExternalWidget_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void User_alias_prevents_generated_alias_and_reports_SVCUIGEN007()
    {
        const string source = """
            using Button = System.Text.StringBuilder;

            public static class Usage
            {
                public static Button CreateAlias() => new Button();
            }
            """;
        var result = RunGenerator(CreateCompilation("UserAlias", source));

        AssertNoErrors(result.OutputCompilation);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN007");
        Assert.Contains("Button", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("using alias", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.GeneratedSources, static generated =>
            generated.Contains("global using Button =", StringComparison.Ordinal));
    }

    [Fact]
    public void Multiple_types_for_a_factory_name_report_SVCUIGEN007()
    {
        const string source = """
            using Avalonia.Controls;

            namespace A { public class Shared { } }
            namespace B { public class Shared { } }
            namespace MyControls { public sealed class Shared : Control { } }

            public static class Usage
            {
                public static object M() => new { X = new A.Shared(), Y = new B.Shared() };
            }
            """;
        var result = RunGenerator(CreateCompilation("MultipleTypeNames", source));

        AssertNoErrors(result.OutputCompilation);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN007");
        Assert.Contains("Shared", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("more than one type", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_files_disappear_when_usage_is_removed()
    {
        var library = CreateExternalWidgetLibrary();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CreateCompilationWithReferences("StaleGeneration", """
            using ExternalWidgetFixture;

            public static class Usage
            {
                public static ExternalWidget Create() => new ExternalWidget();
            }
            """, library);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ExternalAssemblyExtensionsGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        Assert.Contains(
            driver.GetRunResult().Results.Single().GeneratedSources,
            static generated => generated.SourceText.ToString().Contains(
                "ExternalWidgetFixture_ExternalWidget_MarkupExtensions", StringComparison.Ordinal));

        var withoutUsage = CSharpSyntaxTree.ParseText(
            "public static class Usage { }",
            parseOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), withoutUsage);
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            driver.GetRunResult().Results.Single().GeneratedSources,
            static generated => generated.SourceText.ToString().Contains(
                "ExternalWidgetFixture_ExternalWidget_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Global_factory_import_is_generated_with_type_aliases()
    {
        const string source = """
            using System.IO;
            using Avalonia.Controls;

            public static class Usage
            {
                public static string Combine() => Path.Combine("a", "b");
                public static Button Create() => new Button();
            }
            """;
        var result = RunGenerator(CreateCompilation("GlobalFactoryImport", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("global using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("global using Path = global::System.IO.Path;", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("global using Button = global::Avalonia.Controls.Button;", StringComparison.Ordinal));
    }

    [Fact]
    public void Comment_only_edit_keeps_generated_sources_stable_on_one_driver()
    {
        const string source = """
            public static class Usage
            {
                public static Button SetWidth() => new Button().Width(120);
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CreateCompilation("IncrementalOne", source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ExternalAssemblyExtensionsGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var firstResult = driver.GetRunResult().Results.Single();

        // A comment-only edit must not invalidate the cached usage-name inputs.
        var editedTree = CSharpSyntaxTree.ParseText(
            "// unrelated edit\n" + source,
            parseOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), editedTree);
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var secondResult = driver.GetRunResult().Results.Single();

        Assert.Equal(
            firstResult.GeneratedSources.Select(static generated => generated.SourceText.ToString()),
            secondResult.GeneratedSources.Select(static generated => generated.SourceText.ToString()));

        // The same driver was reused across a real syntax-tree replacement, so the second result
        // came from the incremental pipeline rather than a fresh generator run.
        Assert.True(secondResult.TrackedSteps.ContainsKey("SourceOutput"));
        Assert.NotEmpty(secondResult.GeneratedSources);
    }

    private static GeneratorResult RunGenerator(CSharpCompilation compilation)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ExternalAssemblyExtensionsGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var driverDiagnostics,
            TestContext.Current.CancellationToken);
        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(static result => result.GeneratedSources)
            .Select(static generated => generated.SourceText.ToString())
            .ToImmutableArray();

        Assert.Empty(driverDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(runResult.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return new GeneratorResult(
            (CSharpCompilation)outputCompilation,
            generatedSources,
            driverDiagnostics);
    }

    private static PortableExecutableReference EmitReference(CSharpCompilation compilation)
    {
        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
        return MetadataReference.CreateFromImage(ImmutableArray.CreateRange(assemblyStream.ToArray()));
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        string source,
        MetadataReference? additionalReference = null,
        CSharpCompilationOptions? compilationOptions = null)
    {
        var references = additionalReference is null
            ? PlatformReferences
            : PlatformReferences.Add(additionalReference);

        return CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            compilationOptions ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static CSharpCompilation CreateCompilationWithReferences(
        string assemblyName,
        string source,
        params MetadataReference[] additionalReferences) =>
        CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            PlatformReferences.AddRange(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static MetadataReference CreateReference(string path)
    {
        var xmlPath = Path.ChangeExtension(path, ".xml");
        return MetadataReference.CreateFromFile(
            path,
            documentation: File.Exists(xmlPath) ? XmlDocumentationProvider.CreateFromFile(xmlPath) : null);
    }

    private static ImmutableArray<MetadataReference> CreatePlatformReferences()
    {
        var frameworkVersion = Version.Parse(AppContext.TargetFrameworkName![(AppContext.TargetFrameworkName!.LastIndexOf('v') + 1)..]);
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var referencePacksDirectory = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        var referencePack = Directory.EnumerateDirectories(referencePacksDirectory)
            .Where(directory => Path.GetFileName(directory).StartsWith($"{frameworkVersion.Major}.", StringComparison.Ordinal))
            .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No .NET {frameworkVersion.Major} reference pack was found under '{referencePacksDirectory}'.");
        var frameworkReferences = Directory.EnumerateFiles(
                Path.Combine(referencePack, "ref", $"net{frameworkVersion.Major}.0"), "*.dll")
            .Select(static path => CreateReference(path));

        // Use the reference assemblies from the NuGet cache: their XML documentation files sit
        // next to them, which lets in-memory generator tests exercise documented metadata symbols.
        var version = typeof(Control).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        var avaloniaRefRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "avalonia", version, "ref");
        var avaloniaRefDirectory = new[] { $"net{frameworkVersion.Major}.0", "net10.0", "net8.0" }
            .Select(tfm => Path.Combine(avaloniaRefRoot, tfm))
            .First(Directory.Exists);

        return [
            .. frameworkReferences,
            CreateReference(Path.Combine(avaloniaRefDirectory, "Avalonia.Controls.dll")),
            CreateReference(Path.Combine(avaloniaRefDirectory, "Avalonia.Base.dll")),
            CreateReference(typeof(ViewBase).Assembly.Location)
        ];
    }

    private static string CreateSource(string assemblyAttributes, string usage) => $$"""
        using Avalonia;
        using Avalonia.Controls;
        using SVCSystems.Avalonia.CSharpUI;

        {{assemblyAttributes}}

        namespace {{FixtureNamespace}}
        {
            /// <summary>A control used by markup extension generator tests.</summary>
            public class FixtureControl : Control
            {
                /// <summary>The fixture control value property.</summary>
                public static readonly StyledProperty<int> ValueProperty =
                    AvaloniaProperty.Register<FixtureControl, int>(nameof(Value));

                /// <summary>The fixture control value.</summary>
                public int Value
                {
                    get => GetValue(ValueProperty);
                    set => SetValue(ValueProperty, value);
                }
            }
        }

        namespace {{FixtureNamespace}}
        {
        {{usage}}
        }
        """;

    private static string NormalizeGeneratedSource(string source) =>
        string.Join(
            "\n",
            source.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(static line => line.TrimEnd()))
        .TrimEnd('\n');

    private static string NormalizeIgnoringDocumentation(string source) =>
        NormalizeGeneratedSource(string.Join(
            "\n",
            source.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(static line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal))));

    private static void AssertNoErrors(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    private readonly record struct GeneratorResult(
        CSharpCompilation OutputCompilation,
        ImmutableArray<string> GeneratedSources,
        ImmutableArray<Diagnostic> Diagnostics);
}
