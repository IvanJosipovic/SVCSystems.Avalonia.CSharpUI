using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

// Regression coverage for issue #122: explicit type casts inside a compiled-binding
// expression (e.g. x => ((DerivedType)x).Property) used to throw. Since the library
// delegates to Avalonia's native CompiledBinding.Create, casts are supported and the
// binding stays reactive. Numeric value-conversion casts are rejected by Avalonia.
public class CastExpressionBindingTests : AvaloniaTestBase
{
    public class BaseVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class DerivedVm : BaseVm
    {
        private string _name = "initial";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }
    }

    public class DerivedCastView : ViewBase<BaseVm>
    {
        public TextBlock Tb = null!;

        public DerivedCastView(BaseVm vm) : base(vm) { }

        protected override object Build(BaseVm vm) =>
            new TextBlock()
                .Ref(out Tb)
                .Text(vm, x => ((DerivedVm)x).Name);
    }

    [AvaloniaFact]
    public void Cast_BaseToDerived_InExpression_BindsAndStaysReactive()
    {
        var vm = new DerivedVm();
        var view = new DerivedCastView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("initial", view.Tb.Text);

        vm.Name = "updated";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("updated", view.Tb.Text);
    }

    public class BoxedVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private object _boxed = "hello";

        public object Boxed
        {
            get => _boxed;
            set { _boxed = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Boxed))); }
        }
    }

    public class BoxedCastView : ViewBase<BoxedVm>
    {
        public TextBlock Tb = null!;

        public BoxedCastView(BoxedVm vm) : base(vm) { }

        protected override object Build(BoxedVm vm) =>
            new TextBlock()
                .Ref(out Tb)
                .Text(vm, x => (string)x.Boxed);
    }

    [AvaloniaFact]
    public void Cast_ObjectToString_InExpression_BindsAndStaysReactive()
    {
        var vm = new BoxedVm();
        var view = new BoxedCastView(vm);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("hello", view.Tb.Text);

        vm.Boxed = "world";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("world", view.Tb.Text);
    }

    public class NumericVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int _counter = 5;

        public int Counter
        {
            get => _counter;
            set { _counter = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Counter))); }
        }
    }

    public class NumericCastView : ViewBase<NumericVm>
    {
        public Slider Sl = null!;

        public NumericCastView(NumericVm vm) : base(vm) { }

        protected override object Build(NumericVm vm) =>
            new Slider()
                .Ref(out Sl)
                .Value(vm, x => (double)x.Counter);
    }

    [AvaloniaFact]
    public void Cast_IntToDouble_InExpression_Throws()
    {
        var vm = new NumericVm();

        var exception = Assert.Throws<ViewBuildingException>(() => new NumericCastView(vm));
        var inner = Assert.IsAssignableFrom<global::Avalonia.Data.Core.ExpressionParseException>(exception.InnerException);
        Assert.Contains("Convert", inner.Message, StringComparison.Ordinal);
    }
}
