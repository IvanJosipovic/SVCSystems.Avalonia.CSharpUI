using System;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Generates an internal factory in SVCSystems.Avalonia.CSharpUI for a referenced library's
/// compiled XAML resource dictionary or styles. Requires .NET 10 or later.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GenerateXamlResourceAttribute : Attribute
{
    /// <summary>
    /// Initializes a request for a reflection-free compiled XAML factory.
    /// </summary>
    /// <param name="source">An absolute avares URI in a referenced assembly.</param>
    /// <param name="factoryName">The generated class name. Its Create method returns a fresh instance.</param>
    public GenerateXamlResourceAttribute(string source, string factoryName)
    {
        Source = source;
        FactoryName = factoryName;
    }

    /// <summary>Gets the absolute resource URI.</summary>
    public string Source { get; }

    /// <summary>Gets the generated factory class name.</summary>
    public string FactoryName { get; }
}
