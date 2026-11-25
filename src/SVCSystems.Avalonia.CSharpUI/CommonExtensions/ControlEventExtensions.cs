namespace SVCSystems.Avalonia.CSharpUI.CommonExtensions;

/// <summary>
/// Extensions for <see cref="AvaloniaObject"/> events.
/// </summary>
public static class ControlEventExtensions
{
    /// <summary>
    /// Attaches an event handler to the specified control.
    /// </summary>
    /// <typeparam name="TControl">The type of the control, which must be a subclass of <see cref="AvaloniaObject"/>.</typeparam>
    /// <typeparam name="THandler">The type of the event handler.</typeparam>
    /// <param name="control">The control to which the event handler is attached.</param>
    /// <param name="handler">The event handler to attach to the control.</param>
    /// <param name="subscribe">An action that subscribes the event handler to the control.</param>
    /// <returns>The control with the event handler attached.</returns>
    /// <remarks>
    /// Generated event helpers subscribe directly to the original .NET or routed event; this helper
    /// is retained only for compatibility and is no longer emitted into consumer code.
    /// </remarks>
    [System.Obsolete("Generated event helpers subscribe directly; this helper is no longer used by the generator.", false)]
    public static TControl _setEvent<TControl, THandler>(this TControl control, THandler handler, Action<THandler> subscribe)
        where TControl : AvaloniaObject
    {
        subscribe?.Invoke(handler);
        return control;
    }
}