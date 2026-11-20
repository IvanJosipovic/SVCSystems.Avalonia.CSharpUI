namespace SVCSystems.Avalonia.CSharpUI.Samples.Basic.Views;

public sealed class MainView : ViewBase
{
    protected override object Build() =>
        StackPanel()
            .Margin(24)
            .Spacing(12)
            .Children([
                TextBlock()
                    .FontSize(22)
                    .Text("Hello from CSharpUI"),
                Button()
                    .Content("A real Avalonia button")
            ]);
}
