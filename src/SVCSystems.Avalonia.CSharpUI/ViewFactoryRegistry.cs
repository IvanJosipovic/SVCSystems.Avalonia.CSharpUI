using System;
using System.Collections.Concurrent;
using Avalonia.Controls;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Runtime registry used by source-generated control factories.
/// </summary>
public static class ViewFactoryRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<Control>> Factories = new();

    /// <summary>
    /// Registers a factory delegate for a control type.
    /// Intended for source-generated code.
    /// </summary>
    /// <remarks>
    /// Registration is deterministic first-wins: when a factory for <typeparamref name="TControl"/>
    /// already exists the new delegate is ignored and a warning is recorded in
    /// <see cref="Diagnostics.DiagnosticsErrorLog"/>. This avoids making the winner depend on
    /// module-initializer ordering when several assemblies register the same control type.
    /// </remarks>
    public static void Register<TControl>(Func<TControl> factory)
        where TControl : Control
    {
        ArgumentNullException.ThrowIfNull(factory);

        var controlType = typeof(TControl);
        if (!Factories.TryAdd(controlType, () => factory()))
        {
            Diagnostics.DiagnosticsErrorLog.Record(
                Diagnostics.DiagnosticSeverity.Warning,
                Diagnostics.DiagnosticCategory.Other,
                nameof(ViewFactoryRegistry),
                $"A factory for '{controlType.FullName}' is already registered; the first registration wins and this duplicate was ignored.");
        }
    }

    internal static bool TryCreate<TControl>(out TControl control)
        where TControl : Control
    {
        if (Factories.TryGetValue(typeof(TControl), out var factory) && factory() is TControl created)
        {
            control = created;
            return true;
        }

        control = null!;
        return false;
    }
}