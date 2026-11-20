var lifetime = new ClassicDesktopStyleApplicationLifetime
{
    Args = args
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
    Content = MainView(),
    Title = "CSharpUI Basic Sample"
};

lifetime.Start(args);
