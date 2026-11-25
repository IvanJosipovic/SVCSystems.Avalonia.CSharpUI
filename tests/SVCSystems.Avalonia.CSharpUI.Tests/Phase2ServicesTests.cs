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
        Assert.False(((DescriptorCommand)button.Command!).CanExecute(null));

        var withoutDescription = descriptor with { Description = null, CanExecute = true };
        button.Command(withoutDescription);

        Assert.Null(ToolTip.GetTip(button));
        Assert.Null(AutomationProperties.GetHelpText(button));
        Assert.True(((DescriptorCommand)button.Command!).CanExecute(null));
    }

    [Fact]
    public void CommandDescriptorDoesNotOverrideExplicitEnablement()
    {
        var descriptor = new CommandDescriptor { Label = "Save", Execute = () => { } };

        var button = new Button { IsEnabled = false };
        var menuItem = new MenuItem { IsEnabled = false };

        button.Command(descriptor);
        menuItem.MenuCommand(descriptor);

        Assert.False(button.IsEnabled);
        Assert.False(menuItem.IsEnabled);
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
    public void DescriptorCommandForwardsParameterAndCanExecuteChangedThenDetaches()
    {
        var inner = new TrackingCommand { CanExecuteResult = false };
        var adapter = CommandDescriptor.FromICommand(inner, "Save").ToICommand();
        var parameter = new object();

        Assert.False(adapter.CanExecute(parameter));
        Assert.Same(parameter, inner.LastCanExecuteParameter);

        var raises = 0;
        EventHandler handler = (_, _) => raises++;
        adapter.CanExecuteChanged += handler;
        Assert.Equal(1, inner.SubscriberCount);

        inner.RaiseCanExecuteChanged();
        Assert.Equal(1, raises);

        adapter.CanExecuteChanged -= handler;
        Assert.Equal(0, inner.SubscriberCount);
        inner.RaiseCanExecuteChanged();
        Assert.Equal(1, raises);

        inner.CanExecuteResult = true;
        Assert.True(adapter.CanExecute(parameter));
        Assert.Same(parameter, inner.LastCanExecuteParameter);

        var gated = new CommandDescriptor { Label = "Save", Command = inner, CanExecute = false };
        Assert.False(gated.ToICommand().CanExecute(parameter));

        var executing = new CommandDescriptor { Label = "Save", Command = inner, IsExecuting = true };
        Assert.False(executing.ToICommand().CanExecute(parameter));
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
    public void A11yReportNeverExportsTextBoxText()
    {
        const string secret = "CorrectHorseBatteryStaple";
        var root = new StackPanel();
        root.Children.Add(new TextBox { Text = secret, PlaceholderText = "Password" });

        var diagnostics = A11yScanner.Scan(root);

        var finding = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SVCUI_A11Y_003");
        Assert.Null(finding.ChildText);
        Assert.Equal("Password", finding.PlaceholderText);
        Assert.DoesNotContain(secret, A11yReport.ToJson(diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void A11yScannerUsesLabeledByAndTextBlockContentAsNames()
    {
        var root = new StackPanel();
        var label = new TextBlock { Text = "Email" };
        var field = new TextBox();
        AutomationProperties.SetLabeledBy(field, label);
        var button = new Button { Content = new TextBlock { Text = "Send" } };
        root.Children.Add(label);
        root.Children.Add(field);
        root.Children.Add(button);

        var diagnostics = A11yScanner.Scan(root);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SVCUI_A11Y_003");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SVCUI_A11Y_001");
    }

    [Fact]
    public void A11yScannerSkipsDecorativeImagesButReportsUnnamedOnes()
    {
        var root = new StackPanel();
        var decorative = new Image();
        AutomationProperties.SetAccessibilityView(decorative, AccessibilityView.Raw);
        var visible = new Image { Name = "logo" };
        root.Children.Add(decorative);
        root.Children.Add(visible);

        var finding = Assert.Single(A11yScanner.Scan(root),
            diagnostic => diagnostic.Id == "SVCUI_A11Y_002");

        Assert.Equal("logo", finding.AutomationId);
    }

    [Fact]
    public void A11yScannerPrefersConfiguredAutomationIdOverControlName()
    {
        var root = new StackPanel();
        var image = new Image { Name = "xamlName" };
        AutomationProperties.SetAutomationId(image, "configured-id");
        root.Children.Add(image);

        var finding = Assert.Single(A11yScanner.Scan(root));

        Assert.Equal("configured-id", finding.AutomationId);
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

    [Fact]
    public void KeyedBridgeCollapsesDuplicateKeysAlreadyInTarget()
    {
        var first = new Row("a");
        var duplicate = new Row("a");
        var other = new Row("b");
        var target = new ObservableCollection<Row> { first, duplicate, other };
        var bridge = new KeyedCollectionBridge<Row>(target, row => row.Key);

        bridge.Apply([other, new Row("a")]);

        Assert.Equal(2, target.Count);
        Assert.Same(other, target[0]);
        Assert.Same(first, target[1]);
    }

    private sealed record Row(string Key);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TrackingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecuteResult { get; set; } = true;

        public object? LastCanExecuteParameter { get; private set; }

        public int SubscriberCount => CanExecuteChanged?.GetInvocationList().Length ?? 0;

        public bool CanExecute(object? parameter)
        {
            LastCanExecuteParameter = parameter;
            return CanExecuteResult;
        }

        public void Execute(object? parameter)
        {
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
