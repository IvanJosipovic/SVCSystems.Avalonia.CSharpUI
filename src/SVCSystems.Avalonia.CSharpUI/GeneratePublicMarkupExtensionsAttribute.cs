using System;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Marks an assembly as a publisher of public markup extensions.
/// </summary>
/// <remarks>
/// A publishing assembly emits its generated fluent setters, binding helpers, event helpers and
/// style setters as public APIs in the <c>SVCSystems.Avalonia.CSharpUI</c> namespace. Projects that
/// reference a publisher skip generation for the controls it covers and use those extensions
/// instead. This avoids duplicating the same internal extension classes in every compilation and
/// avoids ambiguity when friend assemblies both generate extensions for the same controls.
/// <para>
/// Pass a list of control types to restrict publication to those controls and their base-type
/// closure. With no arguments, every control the generator emits for this assembly is published.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GeneratePublicMarkupExtensionsAttribute : Attribute
{
    /// <summary>
    /// Initializes an attribute that publishes all generated extensions from the assembly.
    /// </summary>
    public GeneratePublicMarkupExtensionsAttribute()
    {
        Controls = Array.Empty<Type>();
    }

    /// <summary>
    /// Initializes an attribute that publishes extensions only for the supplied controls and
    /// their base types.
    /// </summary>
    /// <param name="controls">Root controls whose extensions should be published.</param>
    public GeneratePublicMarkupExtensionsAttribute(params Type[] controls)
    {
        Controls = controls ?? Array.Empty<Type>();
    }

    /// <summary>
    /// Gets the explicitly selected controls, or an empty array when all generated controls are
    /// published.
    /// </summary>
    public Type[] Controls { get; }
}
