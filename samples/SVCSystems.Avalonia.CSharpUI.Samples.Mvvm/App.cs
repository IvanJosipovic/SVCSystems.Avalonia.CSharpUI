using SVCSystems.Avalonia.CSharpUI.Samples.Mvvm.ViewModels;
using SVCSystems.Avalonia.CSharpUI.Samples.Mvvm.Views;

[assembly: GenerateMarkupExtensionsForAvalonia]

var lifetime = new ClassicDesktopStyleApplicationLifetime
{
    Args = args,
    ShutdownMode = ShutdownMode.OnLastWindowClose
};

var appBuilder = AppBuilder.Configure<Application>()
    .UsePlatformDetect()
    .AfterSetup(builder => builder.Instance?.Styles.Add(new FluentTheme()))
#if DEBUG
    .UseHotReload()
#endif
    .SetupWithLifetime(lifetime);

lifetime.MainWindow = new Window
{
    Content = new MainView(new MainViewModel()),
    Title = "CSharpUI MVVM Sample"
};

lifetime.Start(args);
