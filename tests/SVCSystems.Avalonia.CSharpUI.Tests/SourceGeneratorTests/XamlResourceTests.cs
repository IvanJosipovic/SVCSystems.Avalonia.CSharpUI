using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures;

[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource(
    "avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/Resources.axaml", "FixtureResources")]
[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource(
    "avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/Styles.axaml", "FixtureStyles")]
[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource(
    "avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/Services.axaml", "FixtureServices")]
[assembly: SVCSystems.Avalonia.CSharpUI.GenerateXamlResource(
    "avares://SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures/Space%20Folder/Extra.axaml", "EscapedResources")]

namespace SVCSystems.Avalonia.CSharpUI.Tests.SourceGeneratorTests;

public class XamlResourceTests : AvaloniaTestBase
{
    [AvaloniaFact]
    public void Compiled_dictionary_supports_merging_nested_includes_and_theme_variants()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(FixtureResources.Create());

        Assert.True(resources.TryGetResource("Greeting", ThemeVariant.Default, out var greeting));
        Assert.Equal("hello", greeting);
        Assert.True(resources.TryGetResource("Nested", ThemeVariant.Default, out var nested));
        Assert.Equal("nested", nested);
        Assert.True(resources.TryGetResource("Variant", ThemeVariant.Light, out var light));
        Assert.Equal("light", light);
        Assert.True(resources.TryGetResource("Variant", ThemeVariant.Dark, out var dark));
        Assert.Equal("dark", dark);
    }

    [AvaloniaFact]
    public void Factories_return_fresh_mutable_instances()
    {
        var first = FixtureResources.Create();
        var second = FixtureResources.Create();
        Assert.NotSame(first, second);
        first["Greeting"] = "changed";
        Assert.Equal("hello", second["Greeting"]);
        Assert.NotSame(FixtureStyles.Create(), FixtureStyles.Create());
    }

    [AvaloniaFact]
    public void Styles_factory_preserves_compiled_setters()
    {
        var styles = FixtureStyles.Create();
        var style = Assert.IsType<Style>(Assert.Single(styles));
        var setter = Assert.IsType<Setter>(Assert.Single(style.Setters));
        Assert.Equal(Control.TagProperty, setter.Property);
        Assert.Equal("styled", setter.Value);
    }

    [AvaloniaFact]
    public void Compiled_styles_apply_to_controls()
    {
        var button = new Button();
        var window = new Window { Content = button };
        window.Styles.Add(FixtureStyles.Create());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("styled", button.Tag);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Parent_services_are_forwarded_to_compiled_markup()
    {
        var service = new FixtureService();
        var style = Assert.IsType<Style>(Assert.Single(FixtureServices.Create(service)));
        Assert.Same(service, Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
    }

    [AvaloniaFact]
    public void Compiled_factory_errors_are_not_hidden()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => FixtureServices.Create());
        Assert.Contains("parent XAML stack", exception.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Escaped_resource_paths_are_resolved_at_build_time()
    {
        Assert.Equal("escaped path", EscapedResources.Create()["Escaped"]);
    }
}
