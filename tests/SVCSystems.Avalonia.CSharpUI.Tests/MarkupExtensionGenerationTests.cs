using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using SVCSystems.Avalonia.CSharpUI;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator;
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
    public void Static_events_do_not_generate_instance_event_helpers()
    {
        const string source = """
            using Avalonia.Controls;

            namespace MarkupExtensionGeneratorFixture
            {
                public class StaticEventControl : Control
                {
                    public static event System.EventHandler? SharedEvent;
                    public event System.EventHandler? InstanceEvent;
                }
            }
            """;

        var result = RunGenerator(CreateCompilation("StaticEventControl", source));

        AssertNoErrors(result.OutputCompilation);
        var generated = Assert.Single(result.GeneratedSources, generated =>
            generated.Contains(
                "internal static partial class MarkupExtensionGeneratorFixture_StaticEventControl_MarkupExtensions",
                StringComparison.Ordinal));
        Assert.DoesNotContain("OnSharedEvent", generated, StringComparison.Ordinal);
        Assert.Contains("OnInstanceEvent", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Untyped_routed_event_generates_a_compilable_helper()
    {
        const string source = """
            using Avalonia.Controls;
            using Avalonia.Interactivity;
            using SVCSystems.Avalonia.CSharpUI;

            namespace MarkupExtensionGeneratorFixture
            {
                public class UntypedRoutedEventControl : Control
                {
                    public static readonly RoutedEvent ClickEvent =
                        new("Click", RoutingStrategies.Bubble, typeof(UntypedRoutedEventControl), typeof(System.EventHandler<RoutedEventArgs>));

                    public event System.EventHandler<RoutedEventArgs>? Click;
                }
            }
            """;

        var result = RunGenerator(CreateCompilation("UntypedRoutedEvent", source));

        AssertNoErrors(result.OutputCompilation);
        var generated = Assert.Single(result.GeneratedSources, generated =>
            generated.Contains(
                "internal static partial class MarkupExtensionGeneratorFixture_UntypedRoutedEventControl_MarkupExtensions",
                StringComparison.Ordinal));
        Assert.Contains("OnClick", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Declarative_view_base_control_gets_one_set_of_setters()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;
            using Avalonia.Data;
            using SVCSystems.Avalonia.CSharpUI;

            namespace MarkupExtensionGeneratorFixture
            {
                public sealed class BehaviorControl : Control, IDeclarativeViewBase
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<BehaviorControl, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }

                public static class Usage
                {
                    public static void Use(BehaviorControl control, object model)
                    {
                        control.Value(model, x => x);
                        control.Value(CompiledBinding.Create<BehaviorControl, int>(x => x.Value));
                        _ = new Style<BehaviorControl>().Value(1);
                    }
                }
            }
            """;

        var result = RunFullGenerator(CreateCompilation("DeclarativeViewBase", source));

        AssertNoErrors(result.OutputCompilation);
    }

    [Fact]
    public void Referenced_declarative_view_base_control_keeps_all_generated_setters()
    {
        const string librarySource = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            namespace ExternalControls
            {
                public sealed class ExternalBehaviorControl : Control, IDeclarativeViewBase
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<ExternalBehaviorControl, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }
            }
            """;
        var reference = EmitReference(CreateCompilation("ExternalControls", librarySource));

        const string source = """
            using SVCSystems.Avalonia.CSharpUI;

            public static class Usage
            {
                public static void M() => _ = typeof(ExternalControls.ExternalBehaviorControl);
            }
            """;
        var result = RunGenerator(CreateCompilationWithReferences("Consumer", source, reference));

        AssertNoErrors(result.OutputCompilation);
        var generated = Assert.Single(result.GeneratedSources, generated =>
            generated.Contains(
                "ExternalControls_ExternalBehaviorControl_MarkupExtensions",
                StringComparison.Ordinal));
        Assert.Contains("BindingBase", generated, StringComparison.Ordinal);
        Assert.Contains("Style<", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_assembly_generates_public_extensions_and_coverage_metadata()
    {
        var result = RunGenerator(CreateCompilation("Publisher", CreateSource(
            "[assembly: SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensions]",
            string.Empty)));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, source =>
            source.Contains(
                $"public static partial class {FixtureNamespace}_FixtureControl_MarkupExtensions",
                StringComparison.Ordinal));
        var coverage = Assert.Single(result.GeneratedSources, source =>
            source.Contains("PublicMarkupExtensionControlsAttribute", StringComparison.Ordinal));
        Assert.Contains(
            $"Publisher|{FixtureNamespace}.FixtureControl",
            coverage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Consumer_reuses_published_extensions_without_regenerating_them()
    {
        var publisher = RunGenerator(CreateCompilation("Publisher", CreateSource(
            "[assembly: SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensions]",
            string.Empty)));
        AssertNoErrors(publisher.OutputCompilation);

        const string source = """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            public static class Usage
            {
                public static Button Create() => new Button().Width(120);
            }
            """;
        var consumer = RunGenerator(CreateCompilationWithReferences(
            "Consumer",
            source,
            EmitReference(publisher.OutputCompilation)));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.DoesNotContain(consumer.GeneratedSources, generated =>
            generated.Contains("Avalonia_Controls_Button_MarkupExtensions", StringComparison.Ordinal));

        // Factories stay local so the consumer can still create the control by its factory name.
        var factories = Assert.Single(consumer.GeneratedSources, generated =>
            generated.Contains("internal static class GeneratedControlFactories", StringComparison.Ordinal));
        Assert.Contains(" Button(", factories, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_publication_filter_limits_published_controls()
    {
        const string source = """
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            [assembly: GeneratePublicMarkupExtensions(typeof(Button))]

            public static class Usage
            {
                public static TextBlock Create() => new TextBlock { Text = "x" };
            }
            """;
        var result = RunGenerator(CreateCompilation("FilteredPublisher", source));

        AssertNoErrors(result.OutputCompilation);
        var coverage = Assert.Single(result.GeneratedSources, generated =>
            generated.Contains("PublicMarkupExtensionControlsAttribute", StringComparison.Ordinal));
        Assert.Contains("Avalonia.Controls.Button", coverage, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Controls.TextBlock", coverage, StringComparison.Ordinal);

        // Unselected controls must still be generated locally; the filter only limits publication.
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("internal static partial class Avalonia_Controls_TextBlock_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Inaccessible_generic_arguments_are_not_published()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            [assembly: GeneratePublicMarkupExtensions]

            namespace GenericPublishFixture
            {
                internal sealed class InternalModel { }

                public class GenericBase<T> : Control
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<GenericBase<T>, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }

                internal sealed class Derived : GenericBase<InternalModel> { }

                public static class Usage
                {
                    public static object Create() => new Derived().Value(1);
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("GenericPublish", source));

        AssertNoErrors(result.OutputCompilation);
        var coverage = Assert.Single(result.GeneratedSources, generated =>
            generated.Contains("PublicMarkupExtensionControlsAttribute", StringComparison.Ordinal));
        Assert.DoesNotContain("GenericBase", coverage, StringComparison.Ordinal);
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("internal static partial class GenericPublishFixture_GenericBase_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Generic_argument_array_of_internal_type_is_not_published()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            [assembly: GeneratePublicMarkupExtensions]

            namespace ArrayGenericPublishFixture
            {
                internal sealed class InternalModel { }

                public class GenericBase<T> : Control
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<GenericBase<T>, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }

                internal sealed class ArrayDerived : GenericBase<InternalModel[]> { }

                public static class Usage
                {
                    public static object Create() => new ArrayDerived().Value(1);
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("ArrayGenericPublish", source));

        AssertNoErrors(result.OutputCompilation);
        var coverage = Assert.Single(result.GeneratedSources, generated =>
            generated.Contains("PublicMarkupExtensionControlsAttribute", StringComparison.Ordinal));
        Assert.DoesNotContain("InternalModel[]", coverage, StringComparison.Ordinal);
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("internal static partial class ArrayGenericPublishFixture_GenericBase_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_coverage_key_with_global_scope_is_parsed_as_a_whole_key()
    {
        var controls = EmitReference(CreateCompilation("LegacyKeyControls", """
            using Avalonia;
            using Avalonia.Controls;

            namespace LegacyKeyFixture;

            public sealed class PublicModel { }

            public class GenericBase<T> : Control
            {
                public static readonly StyledProperty<int> ValueProperty =
                    AvaloniaProperty.Register<GenericBase<T>, int>(nameof(Value));

                public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
            }
            """));
        var legacyCoverage = CreateLegacyCoverageReference(
            "LegacyCoverageWithGlobalScope",
            "LegacyKeyControls|LegacyKeyFixture.GenericBase<global::LegacyKeyFixture.PublicModel>");

        var consumer = RunGenerator(CreateCompilationWithReferences(
            "LegacyKeyConsumer",
            """
            using LegacyKeyFixture;

            public static class Usage
            {
                public static GenericBase<PublicModel> Create() => new GenericBase<PublicModel>();
            }
            """,
            controls,
            legacyCoverage));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.DoesNotContain(consumer.GeneratedSources, generated =>
            generated.Contains("LegacyKeyFixture_GenericBase_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_coverage_propagated_from_two_assemblies_is_not_an_overlap()
    {
        const string key = "Avalonia.Controls|Avalonia.Controls.Button";
        var consumer = RunGenerator(CreateCompilationWithReferences(
            "LegacyPropagatedCoverageConsumer",
            """
            using Avalonia.Controls;

            public static class Usage
            {
                public static Button Create() => new Button();
            }
            """,
            CreateLegacyCoverageReference("LegacyPublisherA", key),
            CreateLegacyCoverageReference("LegacyPublisherB", key)));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.DoesNotContain(consumer.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN008");
    }

    [Fact]
    public void Known_publisher_origin_replaces_unknown_legacy_origin()
    {
        const string key = "Avalonia.Controls|Avalonia.Controls.Button";
        var knownPublisher = RunGenerator(CreateCompilation("KnownOriginPublisher", CreateSource(
            "[assembly: SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensions]",
            """
            public static class Usage
            {
                public static Button Create() => new Button();
            }
            """)));
        var consumer = RunGenerator(CreateCompilationWithReferences(
            "MixedOriginConsumer",
            """
            using Avalonia.Controls;

            public static class Usage
            {
                public static Button Create() => new Button();
            }
            """,
            CreateLegacyCoverageReference("LegacyPublisher", key),
            EmitReference(knownPublisher.OutputCompilation)));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.DoesNotContain(consumer.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN008");
    }

    [Fact]
    public void Declarative_views_keep_non_overlapping_setters_when_publishing()
    {
        const string publisherSource = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            [assembly: GeneratePublicMarkupExtensions]

            namespace DeclarativePublishFixture
            {
                public sealed class PublishedView : ViewBase
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<PublishedView, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }

                    protected override object Build() => new global::Avalonia.Controls.TextBlock();
                }
            }
            """;
        var publisher = RunGenerator(
            CreateCompilation("DeclarativePublisher", publisherSource),
            new Generator());
        AssertNoErrors(publisher.OutputCompilation);

        // The property/event generators own the value, expression-binding and event setters; the
        // usage-driven generator keeps only the BindingBase and Style<T> overloads nobody else emits.
        Assert.Contains(publisher.GeneratedSources, generated =>
            generated.Contains("public static partial class PublishedViewExtensions", StringComparison.Ordinal));
        var usageDriven = Assert.Single(publisher.GeneratedSources, generated =>
            generated.Contains("public static partial class DeclarativePublishFixture_PublishedView_MarkupExtensions", StringComparison.Ordinal));
        Assert.Contains("/*BindFromBindingSetterGenerator*/", usageDriven, StringComparison.Ordinal);
        Assert.Contains("/*ValueStyleSetterGenerator*/", usageDriven, StringComparison.Ordinal);
        Assert.DoesNotContain("/*ValueSetterGenerator*/", usageDriven, StringComparison.Ordinal);
        Assert.DoesNotContain("/*BindFromExpressionSetterGenerator*/", usageDriven, StringComparison.Ordinal);
        Assert.DoesNotContain("/*ActionToEventGenerator*/", usageDriven, StringComparison.Ordinal);
        var coverage = Assert.Single(publisher.GeneratedSources, generated =>
            generated.Contains("PublicMarkupExtensionControlsAttribute", StringComparison.Ordinal));
        Assert.Contains("DeclarativePublishFixture.PublishedView", coverage, StringComparison.Ordinal);

        const string consumerSource = """
            using Avalonia.Styling;
            using DeclarativePublishFixture;
            using SVCSystems.Avalonia.CSharpUI;

            public sealed class Vm
            {
                public string Name { get; set; } = "";
            }

            public static class Usage
            {
                public static PublishedView Create(Vm vm) => new PublishedView().Value(vm, x => x.Name);
                public static PublishedView Bind() => new PublishedView().Value((Avalonia.Data.BindingBase)null!);
                public static Style<PublishedView> Style() => new Style<PublishedView>().Value(1);
            }
            """;
        var consumer = RunGenerator(
            CreateCompilationWithReferences(
                "DeclarativeConsumer",
                consumerSource,
                EmitReference(publisher.OutputCompilation)),
            new Generator());

        AssertNoErrors(consumer.OutputCompilation);
    }

    [Fact]
    public void Overlapping_publishers_report_SVCUIGEN008()
    {
        PortableExecutableReference CreatePublisher(string name) =>
            EmitReference(RunGenerator(CreateCompilation(name, CreateSource(
                "[assembly: SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensions]",
                """
                public static class Usage
                {
                    public static Button Create() => new Button().Width(120);
                }
                """))).OutputCompilation);

        var consumer = RunGenerator(CreateCompilationWithReferences(
            "OverlapConsumer",
            """
            using Avalonia.Controls;

            public static class Usage
            {
                public static Button Create() => new Button().Width(120);
            }
            """,
            CreatePublisher("FirstPublisher"),
            CreatePublisher("SecondPublisher")));

        var diagnostic = Assert.Single(consumer.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN008");
        Assert.Contains("Avalonia.Controls.Button", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_from_one_publisher_reached_through_two_references_is_not_an_overlap()
    {
        var publisher = RunGenerator(CreateCompilation("UpstreamPublisher", CreateSource(
            "[assembly: SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensions]",
            """
            public static class Usage
            {
                public static Button Create() => new Button().Width(120);
            }
            """)));
        var upstream = EmitReference(publisher.OutputCompilation);

        var propagator = RunGenerator(CreateCompilationWithReferences(
            "PropagatingPublisher",
            CreateSource("[assembly: SVCSystems.Avalonia.CSharpUI.GeneratePublicMarkupExtensions]", string.Empty),
            upstream));
        AssertNoErrors(propagator.OutputCompilation);

        var consumer = RunGenerator(CreateCompilationWithReferences(
            "PropagatedCoverageConsumer",
            """
            using Avalonia.Controls;

            public static class Usage
            {
                public static Button Create() => new Button().Width(120);
            }
            """,
            upstream,
            EmitReference(propagator.OutputCompilation)));

        Assert.DoesNotContain(consumer.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN008");
    }

    [Fact]
    public void Publishing_attributes_expose_their_configuration()
    {
        Assert.Empty(new GeneratePublicMarkupExtensionsAttribute().Controls);

        var filtered = new GeneratePublicMarkupExtensionsAttribute(typeof(Button), typeof(TextBox));
        Assert.Equal([typeof(Button), typeof(TextBox)], filtered.Controls);

        Assert.Empty(new PublicMarkupExtensionControlsAttribute().Controls);
        Assert.Equal(["Assembly|Type"], new PublicMarkupExtensionControlsAttribute("Assembly|Type").Controls);
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

    [Fact]
    public void Constructed_generic_base_types_are_generated()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            namespace GenericFixture
            {
                public class GenericBase<T> : Control
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<GenericBase<T>, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }

                public sealed class Derived : GenericBase<int> { }

                public static class Usage
                {
                    public static Derived Create() => new Derived().Value(1);
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("GenericBaseFixture", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, static source =>
            source.Contains("global::GenericFixture.GenericBase<global::System.Int32>", StringComparison.Ordinal));
    }

    [Fact]
    public void Controls_with_transitive_avalonia_references_are_discovered()
    {
        var vendor = EmitReference(CreateCompilation("VendorControls", """
            using Avalonia.Controls;

            namespace Vendor { public class VendorBase : Control { } }
            """));
        var widgets = EmitReference(CreateCompilationWithReferences("WidgetControls", """
            using Avalonia;
            using Avalonia.Controls;
            using Vendor;

            namespace Widgets
            {
                public sealed class FancyControl : VendorBase
                {
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<FancyControl, int>(nameof(Value));

                    public int Value
                    {
                        get => GetValue(ValueProperty);
                        set => SetValue(ValueProperty, value);
                    }
                }
            }
            """, vendor));
        var consumer = RunGenerator(CreateCompilationWithReferences("TransitiveConsumer", """
            using Widgets;

            public static class Usage
            {
                public static FancyControl Create() => new FancyControl();
            }
            """, widgets, vendor));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.Contains(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class Widgets_FancyControl_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Referenced_control_is_discovered_by_clr_name_when_it_has_a_factory_alias()
    {
        var library = EmitReference(CreateCompilation("AliasedControlLibrary", """
            using Avalonia;
            using Avalonia.Controls;
            using SVCSystems.Avalonia.CSharpUI;

            namespace AliasFixture;

            [SVCSystemsCSharpUIGeneratorMetadata(Alias = "Badge")]
            public sealed class LevelBadge : Control
            {
                public static readonly StyledProperty<int> LevelProperty =
                    AvaloniaProperty.Register<LevelBadge, int>(nameof(Level));

                public int Level
                {
                    get => GetValue(LevelProperty);
                    set => SetValue(LevelProperty, value);
                }
            }
            """));
        var consumer = RunGenerator(CreateCompilationWithReferences("AliasedControlConsumer", """
            using AliasFixture;

            public static class Usage
            {
                public static LevelBadge Create() => new LevelBadge();
            }
            """, library));

        AssertNoErrors(consumer.OutputCompilation);
        Assert.Contains(consumer.GeneratedSources, static source =>
            source.Contains("internal static partial class AliasFixture_LevelBadge_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_control_types_are_generated()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;

            namespace NestedFixture
            {
                public static class Outer
                {
                    public sealed class NestedControl : Control
                    {
                        public static readonly StyledProperty<int> ValueProperty =
                            AvaloniaProperty.Register<NestedControl, int>(nameof(Value));

                        public int Value
                        {
                            get => GetValue(ValueProperty);
                            set => SetValue(ValueProperty, value);
                        }
                    }
                }

                public static class Usage
                {
                    public static object Create() => new Outer.NestedControl();
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("NestedControls", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, static source =>
            source.Contains("internal static partial class NestedFixture_Outer_NestedControl_MarkupExtensions", StringComparison.Ordinal));
    }

    [Fact]
    public void Inaccessible_type_does_not_produce_a_global_alias()
    {
        const string source = """
            using Avalonia.Controls;

            public static class Usage
            {
                private sealed class Button : Control { }

                public static object Create() => new Button();
            }
            """;
        var result = RunGenerator(CreateCompilation("InaccessibleAlias", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN007");
        Assert.DoesNotContain(result.GeneratedSources, static generated =>
            generated.Contains("global using Button =", StringComparison.Ordinal));
    }

    [Fact]
    public void Global_namespace_member_does_not_produce_a_conflicting_alias()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;

            public sealed class Widget : Control
            {
                public static readonly StyledProperty<int> ValueProperty =
                    AvaloniaProperty.Register<Widget, int>(nameof(Value));

                public int Value
                {
                    get => GetValue(ValueProperty);
                    set => SetValue(ValueProperty, value);
                }
            }

            public static class Usage
            {
                public static Widget Create() => new Widget();
            }
            """;
        var result = RunGenerator(CreateCompilation("GlobalNamespaceAlias", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Id == "SVCUIGEN007");
        Assert.DoesNotContain(result.GeneratedSources, static generated =>
            generated.Contains("global using Widget =", StringComparison.Ordinal));
    }

    [Fact]
    public void Registrations_are_generated_despite_factory_name_collisions()
    {
        const string source = """
            using Avalonia;
            using Avalonia.Controls;

            namespace One
            {
                public sealed class Shared : Control
                {
                    internal Shared() { }
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<Shared, int>(nameof(Value));
                    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
                }
            }

            namespace Two
            {
                public sealed class Shared : Control
                {
                    internal Shared() { }
                    public static readonly StyledProperty<int> ValueProperty =
                        AvaloniaProperty.Register<Shared, int>(nameof(Value));
                    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
                }
            }
            """;
        var result = RunGenerator(CreateCompilation("RegistrationCollisions", source));

        AssertNoErrors(result.OutputCompilation);
        Assert.Contains(result.GeneratedSources, static generated =>
            generated.Contains("class One_Shared_ViewFactoryRegistration", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, static generated =>
            generated.Contains("class Two_Shared_ViewFactoryRegistration", StringComparison.Ordinal));
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

    private static GeneratorResult RunFullGenerator(CSharpCompilation compilation) =>
        RunGenerator(compilation, new Generator());

    private static GeneratorResult RunGenerator(
        CSharpCompilation compilation,
        IIncrementalGenerator? generator = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [(generator ?? new ExternalAssemblyExtensionsGenerator()).AsSourceGenerator()],
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

    /// <summary>
    /// Builds a library with the pre-origin coverage attribute format. The runtime library is not
    /// referenced so the test attribute does not clash with the real one.
    /// </summary>
    private static PortableExecutableReference CreateLegacyCoverageReference(string name, params string[] keys)
    {
        var entries = string.Join(", ", keys.Select(static key => $"\"{key}\""));
        var source = $$"""
            using System;

            [assembly: SVCSystems.Avalonia.CSharpUI.PublicMarkupExtensionControlsAttribute({{entries}})]

            namespace SVCSystems.Avalonia.CSharpUI
            {
                [AttributeUsage(AttributeTargets.Assembly)]
                public sealed class PublicMarkupExtensionControlsAttribute : Attribute
                {
                    public PublicMarkupExtensionControlsAttribute(params string[] controls) { }
                }
            }
            """;
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            PlatformReferences.RemoveAt(PlatformReferences.Length - 1),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return EmitReference(compilation);
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

        // Use the reference assemblies from the restored package: their XML documentation files sit
        // next to them, which lets in-memory generator tests exercise documented metadata symbols.
        // The path comes from MSBuild so custom NUGET_PACKAGES/globalPackagesFolder setups work.
        var packagePath = typeof(MarkupExtensionGenerationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => metadata.Key == "SVCSystems.CSharpUI.AvaloniaPackagePath")
            ?.Value;
        var version = typeof(Control).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        var avaloniaRefRoot = !string.IsNullOrWhiteSpace(packagePath)
            ? Path.Combine(packagePath!, "ref")
            : Path.Combine(
                Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
                "avalonia", version, "ref");
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
