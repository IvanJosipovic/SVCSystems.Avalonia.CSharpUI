using System;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Marks controls that should always get generated markup extensions, in addition to controls
/// discovered from application source. Applying this attribute is how the strongly typed list is
/// delivered to the netstandard2.0 generator, which cannot reference Avalonia types directly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class GenerateMarkupControlAttribute : Attribute
{
    public GenerateMarkupControlAttribute(params Type[] controlTypes)
    {
        ControlTypes = controlTypes;
    }

    public Type[] ControlTypes { get; }
}
