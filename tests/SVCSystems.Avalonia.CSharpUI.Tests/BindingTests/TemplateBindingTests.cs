using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SVCSystems.Avalonia.CSharpUI.Tests.BindingTests;

public class TemplateBindingTests : AvaloniaTestBase
{
    public class TemplateTestView : ViewBase
    {
        public static IControlTemplate MyControlTemplate { get; } = new FuncControlTemplate<Button>(
            (control, _) =>
                new TextBox()
                    .Text(control, x => (string?)x.Content, BindingMode.OneWay)
        );

        public Button MyButton = null!;

        protected override object Build() =>
            new Button()
                .Content("Initial")
                .Template(MyControlTemplate)
                .Ref(out MyButton);
    }

    [AvaloniaFact]
    public void TextBox_Text_Bound_To_Button_Content()
    {
        var view = new TemplateTestView();
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = view.MyButton;
        Assert.NotNull(button);
        var textBox = button.GetSelfAndVisualDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);

        Assert.Equal("Initial", textBox.Text);
    }

    [AvaloniaFact]
    public void Changing_Button_Content_Updates_TextBox_Text()
    {
        var view = new TemplateTestView();
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = view.MyButton;
        Assert.NotNull(button);
        var textBox = button.GetSelfAndVisualDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);

        button.Content = "Changed";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Changed", textBox.Text);
    }
}
