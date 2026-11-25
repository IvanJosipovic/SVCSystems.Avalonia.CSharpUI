using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using SVCSystems.Avalonia.CSharpUI.Accessibility;
using SVCSystems.Avalonia.CSharpUI.Async;
using SVCSystems.Avalonia.CSharpUI.Collections;
using SVCSystems.Avalonia.CSharpUI.Commands;
using SVCSystems.Avalonia.CSharpUI.Threading;

namespace SVCSystems.Avalonia.CSharpUI.Tests;

/// <summary>
/// Covers the headless MVVM services adopted from Reactor: explicit async state, command metadata,
/// accessibility diagnostics, change coalescing and keyed collection updates.
/// </summary>
public sealed class Phase2ServicesTests : AvaloniaTestBase
{
    [Fact]
    public void AsyncValueDistinguishesFirstLoadFromRefresh()
    {
        AsyncValue<string> loading = new AsyncValue<string>.Loading();
        AsyncValue<string> data = new AsyncValue<string>.Data("value");
        AsyncValue<string> reloading = new AsyncValue<string>.Reloading("previous");
        AsyncValue<string> error = new AsyncValue<string>.Error(new InvalidOperationException());

        Assert.False(loading.HasValue);
        Assert.True(data.HasValue);
        Assert.True(reloading.HasValue);
        Assert.Equal("previous", reloading.GetValueOrDefault());
        Assert.Equal(
            "stale",
            reloading.Match(() => "loading", _ => "data", _ => "stale", _ => "error"));
        Assert.True(error.IsLoading is false);
    }

    [Fact]
    public void CommandDescriptorBridgesToICommandAndClearsStaleMetadata()
    {
        var descriptor = new CommandDescriptor
        {
            Label = "Save",
            Execute = () => { },
            Description = "Save the document",
            CanExecute = false
        };

        var command = descriptor.ToICommand();
        Assert.False(command.CanExecute(null));

        var button = new Button();
        ToolTip.SetTip(button, "old tooltip");
        AutomationProperties.SetHelpText(button, "old help");
        button.Command(descriptor);

        Assert.Equal("Save", button.Content);
        Assert.Equal("Save the document", ToolTip.GetTip(button));
        Assert.Equal("Save the document", AutomationProperties.GetHelpText(button));
        Assert.False(button.IsEnabled);

        var withoutDescription = descriptor with { Description = null, CanExecute = true };
        button.Command(withoutDescription);

        Assert.Null(ToolTip.GetTip(button));
        Assert.Null(AutomationProperties.GetHelpText(button));
        Assert.True(button.IsEnabled);
    }

    [Fact]
    public void CommandDescriptorWrapsAnExistingViewModelCommand()
    {
        var executions = 0;
        ICommand vmCommand = new DelegateCommand(() => executions++);
        var descriptor = CommandDescriptor.FromICommand(vmCommand, "Refresh");

        descriptor.ToICommand().Execute(null);

        Assert.Equal(1, executions);
    }

    [Fact]
    public void A11yScannerReportsUnnamedControlsWithContext()
    {
        var root = new StackPanel();
        var heading = new TextBlock { Text = "Settings" };
        var button = new Button();
        root.Children.Add(heading);
        root.Children.Add(button);

        var diagnostics = A11yScanner.Scan(root);

        var finding = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SVCUI_A11Y_001");
        Assert.Equal("4.1.2 Name, Role, Value", finding.WcagCriterion);
        Assert.Equal("Settings", finding.NearestHeading);
        Assert.Contains("SetName", finding.CodeSnippet, StringComparison.Ordinal);

        AutomationProperties.SetName(button, "Save");
        Assert.DoesNotContain(
            A11yScanner.Scan(root),
            diagnostic => diagnostic.Id == "SVCUI_A11Y_001");
    }

    [Fact]
    public void A11yReportIsStructuredJson()
    {
        var root = new StackPanel();
        root.Children.Add(new Image());
        var diagnostics = A11yScanner.Scan(root);

        var json = A11yReport.ToJson(diagnostics);

        Assert.Contains("\"bySeverity\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wcagCriterion\"", json, StringComparison.Ordinal);
        Assert.Contains("SVCUI_A11Y_002", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeCoalescerCollapsesMultipleRequestsIntoOnePass()
    {
        var coalescer = new ChangeCoalescer();
        var passes = 0;
        coalescer.Changed += () => passes++;

        coalescer.Request();
        coalescer.Request();
        coalescer.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, passes);
    }

    [Fact]
    public void KeyedBridgeMovesSurvivingItemsInsteadOfRecreatingThem()
    {
        var first = new Row("a");
        var second = new Row("b");
        var target = new ObservableCollection<Row> { first, second };
        var bridge = new KeyedCollectionBridge<Row>(target, row => row.Key);

        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => actions.Add(args.Action);

        bridge.Apply([second, first]);

        Assert.Same(second, target[0]);
        Assert.Same(first, target[1]);
        Assert.Contains(NotifyCollectionChangedAction.Move, actions);
    }

    [Fact]
    public void KeyedBridgeRejectsDuplicateKeysBeforeMutation()
    {
        var target = new ObservableCollection<Row> { new("a") };
        var bridge = new KeyedCollectionBridge<Row>(target, row => row.Key);

        Assert.Throws<InvalidOperationException>(() => bridge.Apply([new Row("x"), new Row("x")]));
        Assert.Single(target);
    }

    private sealed record Row(string Key);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
