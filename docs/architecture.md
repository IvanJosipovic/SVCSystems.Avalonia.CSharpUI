# Architecture

SVCSystems.Avalonia.CSharpUI creates real Avalonia controls. It does not build a virtual tree or reconcile controls after each update. Avalonia remains responsible for control behavior, dependency properties, bindings, styles, layout, and routed events.

The source generator reads Avalonia symbols into a `ControlModel` before emitting fluent APIs. It follows the base-type chain and records declared and inherited members, CLR wrappers, Avalonia property fields, events, attached properties, content slots, and constructors. Property ownership is classified as registered, inherited, added with `AddOwner`, or changed with `OverrideMetadata`. CLR overrides and shadowed members are represented separately.

## Generated APIs

Fluent property setters call the original CLR property or Avalonia property. Expression binding helpers build Avalonia compiled bindings. Event helpers subscribe to the original .NET or routed event. The generated methods are conveniences over Avalonia's types and behavior, and carry XML documentation copied from the underlying CLR property/event (or from the control type for factories). Documentation for referenced controls is read from the `.xml` file shipped beside the reference assembly, because the compiler host does not attach it to metadata symbols.

Generated control factories return the actual control type and attach source metadata:

```csharp
var button = Button().Content("Save");
```

`Button()` works in any source file. The generator emits a global static import of `GeneratedControlFactories` and global aliases for names that are also used as types (for example `Path = System.IO.Path` and `Grid = Avalonia.Controls.Grid`), so factory calls never shadow normal type usage.

`SourceInfo.Get(control)` returns the source file, line, runtime control type, and owning `ViewBase` type when the factory ran during view construction. The metadata is stored separately from the control and does not affect Avalonia property or binding behavior.

## Usage-driven generation

Extensions, control factories and `ViewFactory` registrations are generated only for controls that the application uses. Usage is detected from source: factory calls (`Button()`), object creation (`new Button()`), type references, `typeof`, generic arguments, base lists, and attached-property extension names (`Grid_Row(...)` roots `Grid`). Controls declared in the current project are always included. The resolved set is then closed over base types, because fluent setters are emitted on the type that declares the Avalonia property — `Button().Margin(...)` needs `Layoutable` in the set.

A curated set of common Avalonia controls is generated unconditionally. This list is declared with `typeof(...)` in `[assembly: GenerateMarkupControl(...)]` in the runtime library, so it is compile-time checked; the generator reads it from metadata. The same assembly attribute can be applied in an application as a narrow per-type opt-in, for example to generate extensions for a control used only from compiled XAML in a referenced library.

Generated methods are `internal` to the consuming compilation unless the assembly opts in with `[assembly: GeneratePublicMarkupExtensions]`. A publishing assembly emits public extension classes for the controls it generates (optionally restricted to the controls named in the attribute) and records them in an emitted `PublicMarkupExtensionControlsAttribute`. The control filter limits publication only; every uncovered control is still generated locally. The generator reads the coverage attribute from referenced assemblies and skips local generation for the covered controls, so consumers reuse one public copy instead of each producing an identical internal one. Factories and view registrations are always local, so a consumer retains its own factory for every control it uses.

Only publicly accessible controls are published; the check is recursive over containing types and type arguments, so a constructed generic with an internal argument stays internal. Declarative views are owned by `AvaloniaPropertyExtensionsGenerator` and `AvaloniaEventExtensionsGenerator`, which already emit public setters; the usage-driven generator skips them entirely to avoid ambiguous overloads. Coverage entries carry the originating publisher so propagation through a publisher chain is not mistaken for two independent publishers; overlapping publishers report `SVCUIGEN008`.

If control analysis or emission fails, `SVCUIGEN003` reports the control and error at compile time. When a name matches more than one control type, `SVCUIGEN006` reports the ambiguity and all candidates are generated. When a factory name cannot be given a type alias, `SVCUIGEN007` warns that type usages with that name should be namespace-qualified. See [the usage-driven design](usage-driven-generation.md) for details.

## Generator metadata

`SVCSystemsCSharpUIGeneratorMetadataAttribute` describes exceptional APIs without adding control-name checks to emitters:

- `Ignore = true` suppresses generated APIs for a control or member.
- `Alias = "Caption"` changes a generated factory or fluent method name.
- `GenerateFactory = false` omits a control factory and its `ViewFactory` registration.

The attribute does not rename Avalonia properties, events, or CLR members.

## Compiled XAML resource factories

`[assembly: GenerateXamlResource("avares://Assembly/Path.axaml", "FactoryName")]` asks `XamlResourceGenerator` to emit an internal factory in `SVCSystems.Avalonia.CSharpUI`. The generator locates the referenced assembly's public `CompiledAvaloniaXaml.!AvaloniaResources` `Build:/path` method and calls it through `UnsafeAccessor`, with no reflection or runtime URI loading. `Create(IServiceProvider?)` supplies a parent XAML service provider and returns a fresh `ResourceDictionary` or `Styles` instance. Supported on .NET 10+ only (`SVCUIGEN002`); invalid requests report `SVCUIGEN004` and unavailable or incompatible compiled factories report `SVCUIGEN005`.

## Samples

The solution includes a minimal view with no MVVM dependency, a conventional `INotifyPropertyChanged` sample with typed and two-way bindings, and a local custom control sample.

## AOT and trimming

Generated APIs use statically named Avalonia members. `ViewFactory.Create<TControl>()` preserves the public parameterless constructor for its generic control type and uses generated registrations when available. The package marks the runtime library as AOT-compatible; applications should still build and publish with their target runtime and trimming settings.
