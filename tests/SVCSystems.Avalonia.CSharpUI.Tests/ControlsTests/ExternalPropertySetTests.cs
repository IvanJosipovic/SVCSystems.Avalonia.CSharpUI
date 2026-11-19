using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.ControlsTests;

public class SliderWithLabelViewModel : INotifyPropertyChanged
{
    private double _value;
    private string _label = "";

    public double Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(); }
    }

    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SliderWithLabel : ViewBase<SliderWithLabelViewModel>
{
    private TextBlock _labelTextBlock = null!;
    private Slider _slider = null!;

    public SliderWithLabel(SliderWithLabelViewModel vm) : base(vm) { }

    protected override object Build(SliderWithLabelViewModel vm) =>
        new StackPanel()
            .Children(
                new TextBlock()
                    .Ref(out _labelTextBlock)
                    .Text(vm, x => x.Label),
                new Slider()
                    .Ref(out _slider)
                    .Value(vm, x => x.Value, BindingMode.TwoWay)
            );

    public string GetRealLabelValue() => _labelTextBlock.Text ?? "Not set";

    public double GetSliderValue() => _slider.Value;
}

public class ExternalPropertySetTests : AvaloniaTestBase
{
    [AvaloniaFact]
    public void SliderWithLabel_Label_Binding_Applied()
    {
        var vm = new SliderWithLabelViewModel { Label = "Hello world", Value = 1 };
        var view = new SliderWithLabel(vm);

        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Hello world", view.GetRealLabelValue());
    }

    [AvaloniaFact]
    public void SliderWithLabel_Value_TwoWay_Binding_Works()
    {
        var vm = new SliderWithLabelViewModel { Label = "Test", Value = 1 };
        var view = new SliderWithLabel(vm);

        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.Value = 100;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(100, vm.Value);
        Assert.Equal(100d, view.GetSliderValue());
    }
}
