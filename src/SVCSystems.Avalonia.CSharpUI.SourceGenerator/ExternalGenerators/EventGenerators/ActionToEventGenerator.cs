using System.Text;
using SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExtensionInfos;

namespace SVCSystems.Avalonia.CSharpUI.SourceGenerator.ExternalGenerators.EventGenerators;

internal sealed class ActionToEventGenerator : ExtensionGeneratorBase<EventExtensionInfo>
{
    internal override bool OverlapsWithDeclarativeGenerators => true;

    protected override void AppendExtension(StringBuilder builder, EventExtensionInfo @event)
    {
        var documentation = @event.XmlDoc;
        if (!string.IsNullOrEmpty(documentation))
        {
            builder.Append(documentation);
        }

        if (@event.IsObsolete)
        {
            builder.Append("[Obsolete]").Append(SymbolUtilities.NewLine);
        }

        // events with non-void return types are not supported by the standard EventHandler pattern,
        // so generate a simple extension method that adds the handler and returns the control.
        if (!@event.ReturnsVoid)
        {
            builder.Append($"public static {@event.ReturnType} On{@event.ExtensionName}{@event.GenericArg}(this {@event.ReturnType} control, {@event.EventHandler} handler) {@event.GenericConstraint}\n");
            builder.Append("{\n");
            builder.Append($"    control.{@event.EventName} += handler;\n");
            builder.Append("    return control;\n");
            builder.Append("}\n");
            return;
        }

        var shape = EventAdapterShapes.For(@event.EventParameterTypes, @event.HasStandardSignature);
        var argsString = shape.ParameterList;
        var lambdaParameters = shape.LambdaParameters;
        var actionCall = shape.ActionCall;

        if (@event.IsRoutedEvent && @event.SupportsAddHandler)
        {
            builder.Append($"public static {@event.ReturnType} On{@event.ExtensionName}{@event.GenericArg}(this {@event.ReturnType} control, {argsString}, global::Avalonia.Interactivity.RoutingStrategies? routes = null) {@event.GenericConstraint}\n");
            builder.Append("{\n");
            builder.Append($"    control.AddHandler({@event.ControlTypeName}.{@event.EventName}Event, ({@event.EventHandler})(({lambdaParameters}) => {actionCall}), routes ?? {@event.ControlTypeName}.{@event.EventName}Event.RoutingStrategies);\n");
            builder.Append("    return control;\n");
            builder.Append("}\n");
            return;
        }

        builder.Append($"public static {@event.ReturnType} On{@event.ExtensionName}{@event.GenericArg}(this {@event.ReturnType} control, {argsString}) {@event.GenericConstraint}\n");
        builder.Append("{\n");
        builder.Append($"    control.{@event.EventName} += ({lambdaParameters}) => {actionCall};\n");
        builder.Append("    return control;\n");
        builder.Append("}\n");
    }
}
