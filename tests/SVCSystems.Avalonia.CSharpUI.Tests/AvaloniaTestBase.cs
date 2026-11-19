using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Themes.Simple;
using Avalonia;
using SVCSystems.Avalonia.CSharpUI;

[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace SVCSystems.Avalonia.CSharpUI.Tests;

public abstract class AvaloniaTestBase
{
    static AvaloniaTestBase()
    {
        if (Application.Current is null)
        {
            AppBuilder.Configure<Application>()
                .AfterSetup(builder => builder.Instance?.Styles.Add(new SimpleTheme()))
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true,
                    FrameBufferFormat = PixelFormat.Bgra8888
                })
                .UseViewInitializationStrategy(ViewInitializationStrategy.Lazy)
                .SetupWithoutStarting();
        }
    }

    protected AvaloniaTestBase()
    {
        // [AvaloniaFact] may install its own Application after the static constructor above
        // has run, so ensure the theme on the current instance for every test.
        var app = Application.Current;
        if (app is null)
            return;

        if (!app.Styles.OfType<SimpleTheme>().Any())
            app.Styles.Add(new SimpleTheme());
    }
}
