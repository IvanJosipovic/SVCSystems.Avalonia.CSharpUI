using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class BrushBindingTests : AvaloniaTestBase
{
    public class BrushViewModel : INotifyPropertyChanged
    {
        private IBrush _borderColor = Brushes.Red;

        public IBrush BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class BorderBrushView : ViewBase<BrushViewModel>
    {
        public Border Border = null!;

        public BorderBrushView(BrushViewModel vm) : base(vm) { }

        protected override object Build(BrushViewModel vm) =>
            new Border()
                .Ref(out Border)
                .BorderBrush(vm, x => x.BorderColor)
                .BorderThickness(3);
    }

    [AvaloniaFact]
    public void Border_BorderBrush_ExpressionBinding_Updates()
    {
        var vm = new BrushViewModel();
        var view = new BorderBrushView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Brushes.Red, view.Border.BorderBrush);

        vm.BorderColor = Brushes.Green;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Brushes.Green, view.Border.BorderBrush);
    }
}
