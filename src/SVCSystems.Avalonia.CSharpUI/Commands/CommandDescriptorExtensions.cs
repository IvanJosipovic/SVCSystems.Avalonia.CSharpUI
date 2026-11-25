using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;

namespace SVCSystems.Avalonia.CSharpUI.Commands;

/// <summary>
/// Applies a <see cref="CommandDescriptor"/> to the controls that expose command metadata.
/// Every overload clears metadata that the descriptor does not supply, so a command change cannot
/// leave a stale tooltip, help text or shortcut behind.
/// </summary>
public static class CommandDescriptorExtensions
{
    public static T Command<T>(this T control, CommandDescriptor descriptor)
        where T : Button
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(descriptor);

        control.Command = descriptor.ToICommand();
        control.Content = descriptor.Label;
        control.IsEnabled = descriptor.IsEnabled;
        ToolTip.SetTip(control, descriptor.Description);
        AutomationProperties.SetHelpText(control, descriptor.Description);
        return control;
    }

    public static T MenuCommand<T>(this T control, CommandDescriptor descriptor)
        where T : MenuItem
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(descriptor);

        control.Command = descriptor.ToICommand();
        control.Header = descriptor.Label;
        control.IsEnabled = descriptor.IsEnabled;
        control.InputGesture = descriptor.InputGesture;
        control.Icon = descriptor.Icon;
        ToolTip.SetTip(control, descriptor.Description);
        AutomationProperties.SetHelpText(control, descriptor.Description);
        return control;
    }
}
