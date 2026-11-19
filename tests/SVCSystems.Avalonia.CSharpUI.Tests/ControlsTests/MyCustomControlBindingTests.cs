using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.ControlsTests;

public class MyCustomControlBindingTests : AvaloniaTestBase
{
    public class TestCustomControl : ViewBase
    {
        private string? _newValue;
        private string? _savedValue;
        private bool _canSave;

        public static readonly DirectProperty<TestCustomControl, string?> NewValueProperty =
            AvaloniaProperty.RegisterDirect<TestCustomControl, string?>(nameof(NewValue), o => o.NewValue, (o, v) => o.NewValue = v);

        public string? NewValue
        {
            get => _newValue;
            set { SetAndRaise(NewValueProperty, ref _newValue, value); UpdateCanSave(); }
        }

        public static readonly DirectProperty<TestCustomControl, string?> SavedValueProperty =
            AvaloniaProperty.RegisterDirect<TestCustomControl, string?>(nameof(SavedValue), o => o.SavedValue, (o, v) => o.SavedValue = v);

        public string? SavedValue
        {
            get => _savedValue;
            set { SetAndRaise(SavedValueProperty, ref _savedValue, value); UpdateCanSave(); }
        }

        public static readonly DirectProperty<TestCustomControl, bool> CanSaveProperty =
            AvaloniaProperty.RegisterDirect<TestCustomControl, bool>(nameof(CanSave), o => o.CanSave);

        public bool CanSave
        {
            get => _canSave;
            private set => SetAndRaise(CanSaveProperty, ref _canSave, value);
        }

        public TextBox TextBox = null!;
        public Button SaveButton = null!;
        public Button CancelButton = null!;

        private void UpdateCanSave() => CanSave = SavedValue != NewValue;

        public void Save() => SavedValue = NewValue;

        public void Cancel() => NewValue = SavedValue;

        protected override object Build() =>
            new Grid()
                .Children(
                    new TextBox().Ref(out TextBox).Text(this, x => x.NewValue, BindingMode.TwoWay),
                    new Button().Ref(out CancelButton).IsEnabled(this, x => x.CanSave),
                    new Button().Ref(out SaveButton).IsEnabled(this, x => x.CanSave)
                );
    }

    public class Host : ViewBase
    {
        public TestCustomControl Control = null!;

        protected override object Build() => new TestCustomControl().Ref(out Control);
    }

    [AvaloniaFact]
    public void CustomControl_AvaloniaPropertyBindings_Work()
    {
        var host = new Host();
        var window = new Window { Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var control = host.Control;

        control.NewValue = "A";
        control.SavedValue = "A";
        Dispatcher.UIThread.RunJobs();
        Assert.False(control.CanSave);

        control.NewValue = "B";
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.CanSave);

        control.Save();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(control.SavedValue, control.NewValue);
        Assert.False(control.CanSave);

        control.SavedValue = "C";
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.CanSave);

        control.Cancel();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("C", control.NewValue);
        Assert.False(control.CanSave);
    }
}
