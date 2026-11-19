using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class ExpressionBindingTests : AvaloniaTestBase
{
    public class SeparatedViewState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _stateProp = "Separate state property";

        public string StateProp
        {
            get => _stateProp;
            set
            {
                _stateProp = value;
                OnPropertyChanged();
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class ExpressionBindingTestView : ViewBase<SeparatedViewState>
    {
        public TextBlock MyTextBlock = null!;

        public ExpressionBindingTestView(SeparatedViewState state) : base(state) { }

        protected override object Build(SeparatedViewState state) =>
            new StackPanel().Children(
                new TextBlock()
                    .Ref(out MyTextBlock)
                    .Text(state, x => x.StateProp)
            );
    }

    [AvaloniaFact]
    public void TextBlock_Binding_TextShouldBeUpdatedOnPropertyChanged()
    {
        var state = new SeparatedViewState();
        var view = new ExpressionBindingTestView(state);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        state.StateProp = "Notified!";
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.MyTextBlock);
        Assert.Equal("Notified!", view.MyTextBlock.Text);
    }
}
