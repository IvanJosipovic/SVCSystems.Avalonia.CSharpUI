using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.ControlsTests;

public class ModalHostTests : AvaloniaTestBase
{
    public class ModalHost : ViewBase
    {
        internal Panel? HostPanel;

        public Control? MainContent { get; set; }

        public bool WasMainContentNullDuringBuild { get; private set; }

        protected override object Build()
        {
            var children = new List<Control>();

            WasMainContentNullDuringBuild = MainContent is null;
            if (MainContent is not null)
                children.Add(MainContent);

            children.Add(new Panel());

            var hostPanel = new Panel()
                .Children(children.ToArray());

            HostPanel = hostPanel;
            return hostPanel;
        }
    }

    [AvaloniaFact]
    public void Build_is_called_lazily_on_visual_tree_attachment()
    {
        var host = new ModalHost();
        // MainContent set BEFORE attaching to the visual tree
        host.MainContent = new TextBlock();

        Assert.Null(host.HostPanel);

        var window = new Window { Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(host.HostPanel);
        Assert.False(host.WasMainContentNullDuringBuild);
        Assert.Equal(2, host.HostPanel!.Children.Count);
    }

    [AvaloniaFact]
    public void Children_not_updated_when_setting_MainContent_after_build()
    {
        var host = new ModalHost();
        var window = new Window { Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(host.WasMainContentNullDuringBuild);
        Assert.NotNull(host.HostPanel);
        Assert.Single(host.HostPanel!.Children);

        host.MainContent = new TextBlock();
        Assert.Single(host.HostPanel.Children);
    }
}
