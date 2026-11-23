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

        var eventParameterTypes = @event.EventParameterTypes;
        string argsString;
        string lambdaParameters;
        string actionCall;

        if (eventParameterTypes.Count == 0)
        {
            argsString = "Action action";
            lambdaParameters = string.Empty;
            actionCall = "action()";
        }
        else if (@event.HasStandardSignature)
        {
            argsString = $"Action<{string.Join(", ", eventParameterTypes.Skip(1))}> action";
            lambdaParameters = string.Join(", ", eventParameterTypes.Select((_, index) => $"arg{index}"));
            actionCall = "action(arg1)";
        }
        else if (eventParameterTypes.Count == 1)
        {
            argsString = $"Action<{eventParameterTypes[0]}> action";
            lambdaParameters = "arg0";
            actionCall = "action(arg0)";
        }
        else
        {
            argsString = $"Action<{string.Join(", ", eventParameterTypes)}> action";
            lambdaParameters = string.Join(", ", eventParameterTypes.Select((_, index) => $"arg{index}"));
            actionCall = $"action({lambdaParameters})";
        }

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
