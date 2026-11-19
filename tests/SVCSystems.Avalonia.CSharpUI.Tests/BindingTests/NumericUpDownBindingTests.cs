using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class NumericUpDownBindingTests : AvaloniaTestBase
{
    public class NumericViewModel : INotifyPropertyChanged
    {
        private decimal? _counter = 0m;

        public decimal? Counter
        {
            get => _counter;
            set { _counter = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class NumericView : ViewBase<NumericViewModel>
    {
        public NumericUpDown Num = null!;

        public NumericView(NumericViewModel vm) : base(vm) { }

        protected override object Build(NumericViewModel vm) =>
            new NumericUpDown()
                .Ref(out Num)
                .Value(vm, x => x.Counter, BindingMode.TwoWay);
    }

    [AvaloniaFact]
    public void NumericUpDown_TwoWay_LambdaBinding_Works()
    {
        var vm = new NumericViewModel();
        var view = new NumericView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0m, vm.Counter);
        Assert.Equal((decimal?)0m, view.Num.Value);

        vm.Counter = 5m;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((decimal?)5m, view.Num.Value);

        view.Num.Value = 12m;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(12m, vm.Counter);
    }
}
