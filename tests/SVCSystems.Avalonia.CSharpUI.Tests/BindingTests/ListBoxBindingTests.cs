using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class ListBoxBindingTests : AvaloniaTestBase
{
    public class ListBoxViewModel : INotifyPropertyChanged
    {
        private string _selectedItem = "one";

        public string SelectedItem
        {
            get => _selectedItem;
            set { _selectedItem = value; OnPropertyChanged(); }
        }

        public List<string> Items { get; set; } = new() { "one", "two", "four" };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ListBoxSampleView : ViewBase<ListBoxViewModel>
    {
        public ListBox ListBox = null!;
        public TextBlock SelectedText = null!;

        public ListBoxSampleView(ListBoxViewModel vm) : base(vm) { }

        protected override object Build(ListBoxViewModel vm) =>
            new StackPanel()
                .Children(
                    new ListBox()
                        .Ref(out ListBox)
                        .ItemsSource(vm, x => x.Items)
                        .SelectedItem(vm, x => x.SelectedItem, BindingMode.TwoWay),
                    new TextBlock().Ref(out SelectedText).Text(vm, x => x.SelectedItem)
                );
    }

    [AvaloniaFact]
    public void ListBox_SelectedItem_TwoWay_Works()
    {
        var vm = new ListBoxViewModel();
        var view = new ListBoxSampleView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("one", vm.SelectedItem);
        Assert.Equal("one", (string?)view.ListBox.SelectedItem);

        vm.SelectedItem = "two";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("two", (string?)view.ListBox.SelectedItem);

        view.ListBox.SelectedItem = "four";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("four", vm.SelectedItem);
        Assert.Equal("four", view.SelectedText.Text);
    }
}
