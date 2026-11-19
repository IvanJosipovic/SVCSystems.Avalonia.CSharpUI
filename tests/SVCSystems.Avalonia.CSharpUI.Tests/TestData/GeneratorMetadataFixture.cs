using Avalonia;
using Avalonia.Controls;
using SVCSystems.Avalonia.CSharpUI;

namespace Tests;

[SVCSystemsCSharpUIGeneratorMetadata(Alias = "MetadataFixture")]
public sealed class GeneratorMetadataFixture : Control
{
    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<GeneratorMetadataFixture, string?>(nameof(DisplayName));

    [SVCSystemsCSharpUIGeneratorMetadata(Alias = "Label")]
    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    [SVCSystemsCSharpUIGeneratorMetadata(Ignore = true)]
    public static readonly StyledProperty<string?> InternalNoteProperty =
        AvaloniaProperty.Register<GeneratorMetadataFixture, string?>(nameof(InternalNote));

    public string? InternalNote
    {
        get => GetValue(InternalNoteProperty);
        set => SetValue(InternalNoteProperty, value);
    }
}

public static class GeneratorMetadataFixtureUsage
{
    public static GeneratorMetadataFixture UseAliasedProperty() =>
        SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories.MetadataFixture()
            .Label("compiled fixture");
}
