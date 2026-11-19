using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class StyleBindingTests : AvaloniaTestBase
{
    public class TabVm
    {
        public TabVm(string title, bool enabled = true)
        {
            Title = title;
            Enabled = enabled;
        }

        public string Title { get; }

        public bool Enabled { get; set; }

        public string Content => Title + " Content";
    }

    public class TabsView : ViewBase
    {
        public TabControl TabsControl = null!;

        public List<TabVm> Tabs { get; set; } = new() { new("Tab1"), new("Disabled", false) };

        protected override StyleGroup? BuildStyles() =>
        [
            new Style<TabItem>()
                .IsEnabled(default(TabVm)!, x => x.Enabled)
                .Foreground(Brushes.YellowGreen)
        ];

        protected override object Build() =>
            new TabControl()
                .Ref(out TabsControl)
                .ItemTemplate<TabVm>(tab => new TextBlock().Text(tab.Title))
                .ItemsSource(Tabs);
    }

    public class ScopedTabsView : ViewBase
    {
        public TabControl TabsControl = null!;

        public List<TabVm> Tabs { get; set; } = new() { new("Tab1"), new("Tab2"), new("Disabled", false) };

        protected override StyleGroup? BuildStyles() =>
        [
            new Style<TabItem>(s => s.OfType<TabControl>().Class("sample-tabs").Descendant().OfType<TabItem>())
                .Foreground(Brushes.YellowGreen)
                .IsEnabled(default(TabVm)!, x => x.Enabled)
        ];

        protected override object Build() =>
            new TabControl()
                .Ref(out TabsControl)
                .Classes("sample-tabs")
                .ItemsSource(Tabs)
                .ItemTemplate<TabVm>(tab => new TextBlock().Text(tab.Title))
                .ContentTemplate(
                    new FuncDataTemplate<TabVm>((item, _) =>
                        new TextBlock().Text(item, x => x.Content)));
    }

    [AvaloniaFact]
    public void TabItem_IsEnabled_Binding_Via_Style_Works()
    {
        var view = new TabsView();
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var items = view.TabsControl.GetSelfAndVisualDescendants().OfType<TabItem>().ToList();
        Assert.True(items.Count >= 2);

        var first = items[0];
        var second = items[1];
        Assert.True(first.IsEnabled);
        Assert.False(second.IsEnabled);

        view.Tabs[1].Enabled = true;
        var itemsSrc = view.Tabs.ToList();
        view.TabsControl.ItemsSource = null;
        view.TabsControl.ItemsSource = itemsSrc;
        Dispatcher.UIThread.RunJobs();

        items = view.TabsControl.GetSelfAndVisualDescendants().OfType<TabItem>().ToList();
        second = items[1];
        Assert.True(second.IsEnabled);
    }

    [AvaloniaFact]
    public void TabItem_IsEnabled_Binding_Via_View_Style_Scoped_To_TabControl_Works()
    {
        var view = new ScopedTabsView();
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var items = view.TabsControl.GetSelfAndVisualDescendants().OfType<TabItem>().ToList();
        Assert.True(items.Count >= 3);

        Assert.IsType<TabVm>(items[2].DataContext);
        Assert.Same(view.Tabs[2], items[2].DataContext);
        Assert.Equal(Brushes.YellowGreen, items[2].Foreground);
        Assert.False(items[2].IsEnabled);
    }
}
