# SVCSystems.Avalonia.CSharpUI

[![NuGet](https://img.shields.io/nuget/vpre/SVCSystems.Avalonia.CSharpUI.svg?style=flat-square)](https://www.nuget.org/packages/SVCSystems.Avalonia.CSharpUI)
[![NuGet downloads](https://img.shields.io/nuget/dt/SVCSystems.Avalonia.CSharpUI.svg?style=flat-square)](https://www.nuget.org/packages/SVCSystems.Avalonia.CSharpUI)

SVCSystems.Avalonia.CSharpUI builds user interfaces in C# with real Avalonia controls. Source generation provides fluent setters, strongly typed expression bindings, event helpers, and style setters. Avalonia continues to own the controls, property system, bindings, styles, and events.

## Requirements

- Avalonia 12.x
- .NET 10 or .NET 11

## Install

```bash
dotnet add package SVCSystems.Avalonia.CSharpUI
```

## A view with typed bindings

```csharp
public sealed class GreetingView(GreetingViewModel viewModel) : ViewBase<GreetingViewModel>(viewModel)
{
    protected override object Build(GreetingViewModel vm) =>
        StackPanel()
            .Margin(new Thickness(24))
            .Spacing(12)
            .Children([
                TextBlock()
                    .FontSize(22)
                    .Text(vm, model => model.Greeting),
                TextBox()
                    .PlaceholderText("Type a greeting")
                    .Text(vm, model => model.Greeting, BindingMode.TwoWay)
            ]);
}
```

Generated factory methods create ordinary Avalonia controls and attach their call-site file, line, view type, and control type as source metadata. No `using static` directive is needed: the generator emits a global static import and the type aliases needed to keep names like `Path.Combine` and `new Grid()` working, so `Button()` works in any source file.

Generation is usage-driven: extensions, factories, and registrations are emitted only for controls mentioned in your source (plus their base types) and for a curated list of common Avalonia controls declared with `typeof(...)` in the library. Controls in referenced assemblies are discovered by name without extra attributes. See [the architecture guide](docs/architecture.md) for details.

`SVCSystemsCSharpUIGeneratorMetadataAttribute` can rename generated APIs, suppress generated members or factories, and mark a property as a content slot. For example, `[SVCSystemsCSharpUIGeneratorMetadata(Alias = "Caption")]` renames the generated fluent setter while keeping the underlying Avalonia property unchanged.

## XAML-only library resources

A referenced library may expose a resource dictionary or styles only as compiled XAML. Request a typed, reflection-free factory instead of loading the URI at runtime:

```csharp
using SVCSystems.Avalonia.CSharpUI;

[assembly: GenerateXamlResource("avares://Some.Library/Defaults.axaml", "LibraryResources")]
[assembly: GenerateXamlResource("avares://Some.Library/Styles.axaml", "LibraryStyles")]
```

```csharp
Resources.MergedDictionaries.Add(LibraryResources.Create());
Styles.Add(LibraryStyles.Create());
```

Factories are internal classes in `SVCSystems.Avalonia.CSharpUI`; `Create(IServiceProvider? services = null)` returns a fresh instance and forwards the provider to compiled markup. The generator uses `UnsafeAccessor` against Avalonia's compiled XAML factory convention, so no reflection, runtime URI loading, or dynamic code is involved. Requires .NET 10 or later; diagnostics `SVCUIGEN002` (unsupported target), `SVCUIGEN004` (invalid URI or name), and `SVCUIGEN005` (missing/incompatible compiled factory) report problems at compile time.

## Samples

- `SVCSystems.Avalonia.CSharpUI.Samples.Basic` builds a view without an MVVM framework.
- `SVCSystems.Avalonia.CSharpUI.Samples.Mvvm` shows native `DataContext`, typed bindings, two-way input, and commands.
- `SVCSystems.Avalonia.CSharpUI.Samples.CustomControls` shows generated APIs for a local control, including an aliased property, attached property, and event.

See [the architecture guide](docs/architecture.md) for inheritance handling, external assembly generation, source metadata, and generator overrides.

## Build

```bash
dotnet build
dotnet test
```

The library targets `net10.0` and `net11.0`. The source generator targets `netstandard2.0` so it can run in compiler hosts used by supported IDEs.
