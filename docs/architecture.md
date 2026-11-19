# Architecture

SVCSystems.Avalonia.CSharpUI creates real Avalonia controls. It does not build a virtual tree or reconcile controls after each update. Avalonia remains responsible for control behavior, dependency properties, bindings, styles, layout, and routed events.

The source generator reads Avalonia symbols into a `ControlModel` before emitting fluent APIs. It follows the base-type chain and records declared and inherited members, CLR wrappers, Avalonia property fields, events, attached properties, content slots, and constructors. Property ownership is classified as registered, inherited, added with `AddOwner`, or changed with `OverrideMetadata`. CLR overrides and shadowed members are represented separately.

## Generated APIs

Fluent property setters call the original CLR property or Avalonia property. Expression binding helpers build Avalonia compiled bindings. Event helpers subscribe to the original .NET or routed event. The generated methods are conveniences over Avalonia's types and behavior.

Generated control factories return the actual control type and attach source metadata:

```csharp
using static SVCSystems.Avalonia.CSharpUI.GeneratedControlFactories;

var button = Button().Content("Save");
```

`SourceInfo.Get(control)` returns the source file, line, runtime control type, and owning `ViewBase` type when the factory ran during view construction. The metadata is stored separately from the control and does not affect Avalonia property or binding behavior.

## External controls

Controls declared in the current project are analyzed automatically. To generate extensions for controls in another assembly, add an assembly attribute that names a type from that assembly:

```csharp
[assembly: GenerateMarkupExtensionsForAssembly(typeof(MyControl))]
```

The generated methods are internal by default. Set `generatePublicExtensions: true` to publish them for consumers. If more than one referenced assembly publishes the same Avalonia extension provider, diagnostic `SVCUIGEN001` reports the conflict. If control analysis or emission fails, `SVCUIGEN003` reports the control and error at compile time.

## Generator metadata

`SVCSystemsCSharpUIGeneratorMetadataAttribute` describes exceptional APIs without adding control-name checks to emitters:

- `Ignore = true` suppresses generated APIs for a control or member.
- `Alias = "Caption"` changes a generated factory or fluent method name.
- `GenerateFactory = false` omits a control factory and its `ViewFactory` registration.
- `Content = true` marks a property as a content slot for generator tooling.

The attribute does not rename Avalonia properties, events, or CLR members.

## Compiled XAML resource factories

`[assembly: GenerateXamlResource("avares://Assembly/Path.axaml", "FactoryName")]` asks `XamlResourceGenerator` to emit an internal factory in `SVCSystems.Avalonia.CSharpUI`. The generator locates the referenced assembly's public `CompiledAvaloniaXaml.!AvaloniaResources` `Build:/path` method and calls it through `UnsafeAccessor`, with no reflection or runtime URI loading. `Create(IServiceProvider?)` supplies a parent XAML service provider and returns a fresh `ResourceDictionary` or `Styles` instance. Supported on .NET 10+ only (`SVCUIGEN002`); invalid requests report `SVCUIGEN004` and unavailable or incompatible compiled factories report `SVCUIGEN005`.

## Samples

The solution includes a minimal view with no MVVM dependency, a conventional `INotifyPropertyChanged` sample with typed and two-way bindings, and a local custom control sample.

## AOT and trimming

Generated APIs use statically named Avalonia members. `ViewFactory.Create<TControl>()` preserves the public parameterless constructor for its generic control type and uses generated registrations when available. The package marks the runtime library as AOT-compatible; applications should still build and publish with their target runtime and trimming settings.
