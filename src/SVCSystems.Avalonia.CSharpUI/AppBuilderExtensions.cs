using Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Provides extension methods for configuring an <see cref="AppBuilder"/>.
/// </summary>
public static class AppBuilderExtensions
{
    private static Func<Type, Control>? _controlFactory;
    private static IServiceProvider? _serviceProvider;
    private static ViewInitializationStrategy _defaultViewInitializationStrategy = ViewInitializationStrategy.Lazy;

    // The settings remain process-wide (public API unchanged); the owners below make
    // conflicting configuration from a second AppBuilder an explicit error instead of a
    // silent overwrite. Weak references avoid keeping short-lived builders alive.
    private static readonly object NoAppBuilderOwner = new();
    private static WeakReference<object>? _controlFactoryOwner;
    private static WeakReference<object>? _serviceProviderOwner;
    private static WeakReference<object>? _initializationStrategyOwner;

    internal static Func<Type, Control>? ControlFactory => _controlFactory;

    /// <summary>
    /// Gets the current <see cref="IServiceProvider"/> instance.
    /// </summary>
    internal static IServiceProvider? ServiceProvider => _serviceProvider;

    /// <summary>
    /// Gets the default <see cref="ViewInitializationStrategy"/> used by views when not explicitly specified.
    /// </summary>
    internal static ViewInitializationStrategy DefaultViewInitializationStrategy => _defaultViewInitializationStrategy;

    /// <summary>
    /// Configures the <see cref="AppBuilder"/> to use the specified <see cref="IServiceProvider"/>.
    /// </summary>
    /// <param name="appBuilder">The <see cref="AppBuilder"/> to configure.</param>
    /// <param name="serviceProvider">The <see cref="IServiceProvider"/> to use.</param>
    /// <returns>The configured <see cref="AppBuilder"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a different service provider was already configured by another
    /// <see cref="AppBuilder"/>. Passing the same instance again is idempotent.
    /// </exception>
    public static AppBuilder UseServiceProvider(this AppBuilder appBuilder, IServiceProvider serviceProvider)
    {
        SetConfigurationOnce(
            nameof(ServiceProvider), serviceProvider, ref _serviceProvider, ref _serviceProviderOwner, appBuilder,
            useReferenceIdentity: true);
        return appBuilder;
    }


    /// <summary>
    /// Configures the application to use a custom factory for creating controls.
    /// </summary>
    /// <remarks>Use this method to provide a custom control creation strategy. This is useful
    /// when integrating with dependency injection or customizing control instantiation.</remarks>
    /// <param name="appBuilder">The application builder to configure.</param>
    /// <param name="factory">A delegate that creates a control instance for the specified component type. Cannot be null.</param>
    /// <returns>The configured application builder instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a different control factory was already configured by another
    /// <see cref="AppBuilder"/>. Passing the same delegate again is idempotent.
    /// </exception>
    public static AppBuilder UseControlFactory(this AppBuilder appBuilder, Func<Type, Control> factory)
    {
        SetConfigurationOnce(
            nameof(ControlFactory), factory, ref _controlFactory, ref _controlFactoryOwner, appBuilder);
        return appBuilder;
    }

    /// <summary>
    /// Configures the default initialization strategy for views.
    /// </summary>
    /// <param name="appBuilder">The <see cref="AppBuilder"/> to configure.</param>
    /// <param name="strategy">The <see cref="ViewInitializationStrategy"/> to use as default for all views.</param>
    /// <returns>The configured <see cref="AppBuilder"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a different strategy was already configured by another
    /// <see cref="AppBuilder"/>. Passing the same strategy again is idempotent.
    /// </exception>
    public static AppBuilder UseViewInitializationStrategy(this AppBuilder appBuilder, ViewInitializationStrategy strategy)
    {
        SetConfigurationOnce(
            nameof(DefaultViewInitializationStrategy), strategy, ref _defaultViewInitializationStrategy,
            ref _initializationStrategyOwner, appBuilder);
        return appBuilder;
    }

    /// <summary>
    /// Enables or disables hot reload functionality for the application.
    /// </summary>
    /// <remarks>This toggle is process-wide and intentional; unlike the settings above it is not latched.</remarks>
    /// <param name="appBuilder">The <see cref="AppBuilder"/> to configure.</param>
    /// <param name="enable">A value indicating whether to enable hot reload. Defaults to <c>true</c>.</param>
    /// <returns>The configured <see cref="AppBuilder"/>.</returns>
    public static AppBuilder UseHotReload(this AppBuilder appBuilder, bool enable = true)
    {
        if (enable)
        {
            HotReloadManager.Activate();
        }
        else
        {
            HotReloadManager.Deactivate();
        }
        return appBuilder;
    }

    /// <summary>
    /// Applies a process-wide setting once, rejecting conflicting values with a clear error.
    /// </summary>
    private static void SetConfigurationOnce<T>(
        string configurationName,
        T value,
        ref T field,
        ref WeakReference<object>? ownerReference,
        AppBuilder appBuilder,
        bool useReferenceIdentity = false)
    {
        // Service providers are identified by instance: overriding Equals must not let a different
        // provider bypass the "same instance is idempotent" rule. Strategies and delegates keep
        // their normal value/delegate equality.
        var sameValue = useReferenceIdentity
            ? ReferenceEquals(field, value)
            : EqualityComparer<T>.Default.Equals(field, value);

        if (ownerReference is not null && !sameValue)
        {
            ownerReference.TryGetTarget(out var owner);
            var fromAnotherBuilder = !ReferenceEquals(owner, appBuilder);

            if (fromAnotherBuilder)
            {
                throw new InvalidOperationException(
                    $"CSharpUI configuration '{configurationName}' is process-wide and was already set by another AppBuilder. " +
                    "Setting a different value is not supported; pass the same value again (idempotent) or configure a single AppBuilder.");
            }
        }

        field = value;
        ownerReference ??= new WeakReference<object>((object?)appBuilder ?? NoAppBuilderOwner);
    }
}
