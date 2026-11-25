using System;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Generates an internal factory in SVCSystems.Avalonia.CSharpUI for a referenced library's
/// compiled XAML resource dictionary or styles. Requires .NET 10 or later.
/// </summary>
/// <remarks>
/// The generated factory reaches the referenced assembly's compiled-XAML <c>Build:/...</c> method
/// through <see cref="System.Runtime.CompilerServices.UnsafeAccessorAttribute"/>, so no runtime
/// reflection is needed and the call is trimming/AOT friendly as long as the accessor target
/// survives. Callers must reference the resource library at build time (only referenced
/// assemblies are scanned) and must keep its generated compiled-XAML resources; trimming away
/// the accessor target shows up as a missing-member warning from the trimmer/AOT compiler.
/// </remarks>
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
