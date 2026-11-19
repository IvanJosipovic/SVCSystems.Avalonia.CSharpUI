using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests;

public class ReloadTests : AvaloniaTestBase
{
    private sealed class StyledReloadView : ViewBase
    {
        public int BuildCount { get; private set; }

        protected override StyleGroup? BuildStyles() =>
        [
            new Style<Button>().Background(Brushes.Red)
        ];

        protected override object Build()
        {
            BuildCount++;
            return new Button();
        }
    }

    [AvaloniaFact]
    public void Reload_does_not_accumulate_view_styles()
    {
        var view = new StyledReloadView();
        _ = view.Child; // force lazy initialization

        var externalStyle = new Style<Button>().Background(Brushes.Blue);
        view.Styles.Add(externalStyle);
        var initialCount = view.Styles.Count;
        Assert.True(initialCount >= 2);

        view.Reload();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, view.BuildCount);
        Assert.Equal(initialCount, view.Styles.Count);
        Assert.Contains(externalStyle, view.Styles);
    }

    [AvaloniaFact]
    public void Repeated_reloads_keep_style_count_stable()
    {
        var view = new StyledReloadView();
        _ = view.Child;
        var initialCount = view.Styles.Count;

        for (var i = 0; i < 3; i++)
        {
            view.Reload();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(initialCount, view.Styles.Count);
        }

        Assert.Equal(4, view.BuildCount);
    }
}
