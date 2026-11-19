using Avalonia;
using Avalonia.Interactivity;

namespace SVCSystems.Avalonia.CSharpUI.Tests.ControlsTests;

public class NonInteractiveRoutedEventExtensionTests
{
    [Fact]
    public void Routed_event_on_non_interactive_avalonia_object_compiles()
    {
        var control = new NonInteractiveRoutedEventControl();
        control.OnColumnChanged(_ => { });
    }
}

public class NonInteractiveRoutedEventControl : AvaloniaObject
{
    public static readonly RoutedEvent<RoutedEventArgs> ColumnChangedEvent =
        new(nameof(ColumnChanged), RoutingStrategies.Bubble, typeof(NonInteractiveRoutedEventControl));

    public event EventHandler<RoutedEventArgs>? ColumnChanged;

    public void RaiseColumnChanged() => ColumnChanged?.Invoke(this, new RoutedEventArgs());
}
