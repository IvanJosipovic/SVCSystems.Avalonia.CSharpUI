using System.Windows.Input;
using Avalonia.Input;

namespace SVCSystems.Avalonia.CSharpUI.Commands;

/// <summary>
/// Describes a command once so its label, icon, shortcut, tooltip and help text can be applied
/// consistently to buttons, menu items and context menus. It bridges to the existing
/// <see cref="ICommand"/> contract rather than replacing it, so ordinary view models keep working.
/// </summary>
public sealed record CommandDescriptor
{
    public required string Label { get; init; }

    /// <summary>An existing view-model command. When present it is used for execution and state.</summary>
    public ICommand? Command { get; init; }

    public Action? Execute { get; init; }

    public Func<Task>? ExecuteAsync { get; init; }

    public bool CanExecute { get; init; } = true;

    public bool IsExecuting { get; init; }

    /// <summary>Tooltip and automation help text. Null means "clear any previous value".</summary>
    public string? Description { get; init; }

    public object? Icon { get; init; }

    public KeyGesture? InputGesture { get; init; }

    public string? AccessKey { get; init; }

    public bool IsEnabled => CanExecute && !IsExecuting && (Command?.CanExecute(null) ?? true);

    /// <summary>Creates a descriptor from an existing <see cref="ICommand"/>.</summary>
    public static CommandDescriptor FromICommand(
        ICommand command,
        string label,
        string? description = null,
        KeyGesture? inputGesture = null,
        object? icon = null,
        string? accessKey = null) => new()
    {
        Label = label,
        Command = command ?? throw new ArgumentNullException(nameof(command)),
        Description = description,
        InputGesture = inputGesture,
        Icon = icon,
        AccessKey = accessKey
    };

    /// <summary>
    /// Wraps the descriptor in an <see cref="ICommand"/> that reads its current state on every
    /// <c>CanExecute</c> call. Call <see cref="DescriptorCommand.RaiseCanExecuteChanged"/> after
    /// changing the descriptor.
    /// </summary>
    public DescriptorCommand ToICommand() => new(this);
}

/// <summary>
/// An <see cref="ICommand"/> adapter over a <see cref="CommandDescriptor"/>.
/// </summary>
public sealed class DescriptorCommand : ICommand
{
    private readonly CommandDescriptor _descriptor;

    internal DescriptorCommand(CommandDescriptor descriptor) => _descriptor = descriptor;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _descriptor.IsEnabled;

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        if (_descriptor.Command is { } existing)
        {
            existing.Execute(parameter);
            return;
        }

        if (_descriptor.Execute is { } execute)
        {
            execute();
            return;
        }

        if (_descriptor.ExecuteAsync is { } executeAsync)
        {
            _ = executeAsync();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
