using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class CommandBindingTests : AvaloniaTestBase
{
    private record struct CmdObject(string MyProperty);

    private class Vm : INotifyPropertyChanged
    {
        private CmdObject _myObject = new("Start");

        public CmdObject MyObject
        {
            get => _myObject;
            set
            {
                if (_myObject.Equals(value)) return;
                _myObject = value;
                OnPropertyChanged();
            }
        }

        public void MyCommand(object? param) => MyObject = new($"Param:{param is Vm}");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private class CommandView : ViewBase<Vm>
    {
        private readonly Vm _vm;

        public TextBlock Text = null!;
        public Button Button = null!;

        public CommandView(Vm vm) : base(vm) { _vm = vm; }

        protected override object Build(Vm vm) =>
            new StackPanel()
                .Children(
                    new TextBlock().Ref(out Text).Text(vm, x => x.MyObject.MyProperty),
                    new Button()
                        .Ref(out Button)
                        .Content("Execute Command")
                        .OnClick(_ => _vm.MyCommand(_vm))
                );
    }

    [AvaloniaFact]
    public void Button_Click_Updates_Text_Via_ViewModel()
    {
        var vm = new Vm();
        var view = new CommandView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Start", view.Text.Text);

        view.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Param:True", view.Text.Text);
    }
}
