using Avalonia.Controls;
using System.Diagnostics.CodeAnalysis;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Factory for creating control instances with optional dependency injection.
/// </summary>
public static class ViewFactory
{
    /// <summary>
    /// Creates a control instance using the configured control factory
    /// or falls back to parameterless constructor.
    /// </summary>
    /// <typeparam name="TControl">
    /// The control type to create. The annotation preserves its public parameterless constructor
    /// under trimming/AOT so the <c>Activator.CreateInstance&lt;TControl&gt;()</c> fallback keeps working.
    /// </typeparam>
    /// <remarks>
    /// Resolution order is: custom control factory, <see cref="IServiceProvider"/>, generated
    /// registry, then direct parameterless construction. Generated factories and registered
    /// factories use normal constructor calls and need no reflection, but the annotation on
    /// <typeparamref name="TControl"/> is required on this whole API because the final fallback
    /// uses <c>Activator.CreateInstance</c>. Callers that wrap <see cref="Create{TControl}"/> in
    /// their own generic helpers must propagate the annotation to their type parameter (or use a
    /// concrete type whose parameterless constructor is preserved by trimming).
    /// </remarks>
    public static TControl Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TControl>() where TControl : Control
    {
        if (AppBuilderExtensions.ControlFactory is { } factory)
            return (TControl)factory(typeof(TControl));

        if (AppBuilderExtensions.ServiceProvider?.GetService(typeof(TControl)) is TControl serviceControl)
            return serviceControl;

        if (ViewFactoryRegistry.TryCreate<TControl>(out var registeredControl))
            return registeredControl;

        return System.Activator.CreateInstance<TControl>();
    }
}
