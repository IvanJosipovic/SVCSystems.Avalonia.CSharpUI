using System;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Opts a compilation into a generated <c>global using static GeneratedControlFactories</c> so
/// control factories such as <c>Button()</c> are available in every source file without an
/// explicit import.
/// </summary>
/// <remarks>
/// This is opt-in on purpose. A generated global static import injects bare factory names into
/// every file and can shadow type names; tooling that does not reproduce the generated compilation
/// (for example a formatter simplifying type references) cannot reliably account for it.
/// Prefer a per-file <c>using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;</c>
/// or a hand-written project-level <c>&lt;Using Static="..." /&gt;</c> item. Use this attribute only
/// when the whole project intentionally wants bare factories everywhere.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GenerateGlobalControlFactoriesAttribute : Attribute;
