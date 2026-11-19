using SVCSystems.Avalonia.CSharpUI.Samples.Mvvm.ViewModels;
using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;

namespace SVCSystems.Avalonia.CSharpUI.Samples.Mvvm.Views;

public sealed class MainView(MainViewModel viewModel) : ViewBase<MainViewModel>(viewModel)
{
    protected override object Build(MainViewModel vm) =>
        StackPanel()
            .Margin(new Thickness(24))
            .Spacing(12)
            .Children([
                TextBlock()
                    .FontSize(22)
                    .Text(vm, model => model.Greeting),
                TextBox()
                    .PlaceholderText("Edit the greeting")
                    .Text(vm, model => model.Greeting, BindingMode.TwoWay),
                TextBlock()
                  .Text(vm, model => model.ClickCountText),
                Button()
                    .Content("Increment")
                    .Command(vm, model => model.IncrementCommand)
            ]);
}
