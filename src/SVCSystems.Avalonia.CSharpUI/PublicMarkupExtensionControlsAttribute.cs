using System;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Records the controls whose generated markup extensions are publicly available from this
/// assembly.
/// </summary>
/// <remarks>
/// The source generator emits this assembly attribute into publishing assemblies. Consumers read
/// it from their references so they can skip generating duplicate extensions for the same
/// controls. Each entry is a <c>TypeKey</c> of the form <c>AssemblyName|Namespace.Type</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class PublicMarkupExtensionControlsAttribute : Attribute
{
    /// <summary>
    /// Initializes the attribute with the covered control keys.
    /// </summary>
    /// <param name="controls">Control keys whose extensions this assembly publishes.</param>
    public PublicMarkupExtensionControlsAttribute(params string[] controls)
    {
        Controls = controls ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the keys of the controls whose extensions are published by this assembly.
    /// </summary>
    public string[] Controls { get; }
}
