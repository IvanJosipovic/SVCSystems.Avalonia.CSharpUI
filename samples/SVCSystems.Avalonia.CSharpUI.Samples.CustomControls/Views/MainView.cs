using SVCSystems.Avalonia.CSharpUI.Samples.CustomControls.Controls;

namespace SVCSystems.Avalonia.CSharpUI.Samples.CustomControls.Views;

public sealed class MainView : ViewBase
{
    protected override object Build() =>
        StackPanel()
            .Margin(new Thickness(24))
            .Spacing(12)
            .Children([
                TextBlock().Text("Generated APIs for a local custom control:"),
                LevelBadge()
                    .Importance(BadgeLevel.Important)
                    .Content("Important message")
                    .OnActivated(_ => { }),
                TextBlock()
                    .Text("Attached property applies to this label")
                    .LevelBadge_Emphasized(true)
            ]);
}
