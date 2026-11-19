using System;
using System.Collections.Generic;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.XamlIl.Runtime;

namespace SVCSystems.Avalonia.CSharpUI.Tests.XamlFixtures;

public sealed class ServiceValueExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var stack = serviceProvider.GetService(typeof(IAvaloniaXamlIlParentStackProvider))
            as IAvaloniaXamlIlParentStackProvider;
        if (stack is not null)
        {
            foreach (var parent in stack.Parents)
            {
                if (parent is FixtureService service)
                    return service;
            }
        }

        throw new InvalidOperationException("The parent XAML stack did not contain FixtureService.");
    }
}

public sealed class FixtureService : IServiceProvider, IAvaloniaXamlIlParentStackProvider
{
    public IEnumerable<object> Parents => new[] { this };

    public object? GetService(Type serviceType) =>
        serviceType == typeof(IAvaloniaXamlIlParentStackProvider) ? this : null;
}
