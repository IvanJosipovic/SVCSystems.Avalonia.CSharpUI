using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SVCSystems.Avalonia.CSharpUI;

/// <summary>
/// Defines when a view should be initialized.
/// </summary>
public enum ViewInitializationStrategy
{
    /// <summary>
    /// View is initialized lazily when first accessed (e.g., when Child property is accessed or when attached to visual tree).
    /// </summary>
    Lazy,

    /// <summary>
    /// View is initialized immediately in the constructor.
    /// </summary>
    Immediate
}

public abstract class ViewBase<TViewModel> : ViewBase
    where TViewModel : class
{
    public virtual TViewModel? ViewModel
    {
        get => (TViewModel?)DataContext!;
        set => DataContext = value;
    }

    //injecting viewmodel through constructor is optional, but if you do it, the view will be initialized immediately with provided viewmodel as DataContext
    protected ViewBase(TViewModel viewModel) : base(ViewInitializationStrategy.Lazy)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Initialize();
    }

    //classic mvvm approach with parameterless constructor and setting DataContext later is also supported, but in this case the view will be initialized lazily when first accessed or attached to visual tree
    protected ViewBase() : base(ViewInitializationStrategy.Lazy)
    {
        // not calling Initialize()! Waiting for OnDataContextChanged
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // once DataContext is set to a compatible viewmodel type, we can initialize the view if it hasn't been initialized yet
        if (DataContext is TViewModel && !IsInitialized)
        {
            Initialize();
        }
    }

    protected abstract object Build(TViewModel vm);

    protected override object Build()
    {
        if (DataContext is not TViewModel vm)
        {
            throw new InvalidOperationException(
                $"Cannot build view {GetType().Name} without a valid ViewModel of type {typeof(TViewModel).Name}.");
        }

        return Build(vm);
    }
}

/// <summary>
/// Base view class used like UserControl in XAML
/// </summary>
public abstract class ViewBase : Decorator, IReloadable, IDeclarativeViewBase
{
    private INameScope? _nameScope;
    private IReadOnlyList<IStyle>? _viewStyles;

    // Identity hash code (not the overridable GetHashCode) so each view gets a stable id
    // even when a view type overrides equality/hashing.
    private readonly string _instanceId;

    /// <summary>
    /// Current NameScope of this view
    /// </summary>
    protected INameScope Scope => _nameScope ??= new NameScope();

    public event Action? ViewInitialized;

    protected abstract object Build();

    protected virtual StyleGroup? BuildStyles() => null;

    protected ViewBase()
        : this(AppBuilderExtensions.DefaultViewInitializationStrategy)
    {
    }

    protected ViewBase(ViewInitializationStrategy initializationStrategy)
    {
        _instanceId = RuntimeHelpers.GetHashCode(this).ToString("x8", CultureInfo.InvariantCulture);
        InitializationStrategy = initializationStrategy;

        if (initializationStrategy == ViewInitializationStrategy.Immediate)
            Initialize();
    }

    /// <summary>
    /// Stable per-instance source label used to correlate diagnostics from concurrently failing views.
    /// </summary>
    private string DiagnosticSource => $"{GetType().Name}#{_instanceId}";

    /// <summary>
    /// Gets the initialization strategy used by this view.
    /// </summary>
    public ViewInitializationStrategy InitializationStrategy { get; }

    private bool _isInitialized;
    private bool _isInitializing;

    private void EnsureInitialized()
    {
        if (!_isInitialized && !_isInitializing)
        {
            Initialize();
        }
    }

    /// <summary>
    /// Called from constructor, right before initialization and building UI
    /// Override this method when you want to run some stuff before creation of children controls
    /// </summary>
    protected virtual void OnCreated()
    {
    }

    protected virtual void OnAfterInitialized()
    {
    }

    /// <summary>
    /// Builds the view once. Subsequent calls are ignored unless a reload has forced a rebuild.
    /// </summary>
    public void Initialize() => InitializeCore(force: false);

    private void InitializeCore(bool force)
    {
        if (!force && (_isInitialized || _isInitializing))
            return;

        _isInitializing = true;

        try
        {
            using var sourceScope = SourceInfo.EnterView(GetType());
            OnCreated();
            NameScope.SetNameScope(this, Scope);

            if (BuildStyles() is { } styleGroup)
            {
                var viewStyles = StyleBuilder.StylesToRange(styleGroup).ToImmutableList();
                Styles.AddRange(viewStyles);
                _viewStyles = viewStyles;
            }

            var content = Build();
            if (content is not Control builtControl)
            {
                var actualType = content?.GetType().FullName ?? "<null>";
                var message = $"Build() in {GetType().FullName} returned '{actualType}', but a {nameof(Control)} instance is required for Child.";
                Diagnostics.DiagnosticsErrorLog.Record(
                    Diagnostics.DiagnosticSeverity.Error, Diagnostics.DiagnosticCategory.Build, DiagnosticSource, message);
                throw new ViewBuildingException(message, new InvalidOperationException(message));
            }

            Child = builtControl;

            _isInitialized = true;
            ViewInitialized?.Invoke();
            OnAfterInitialized();
        }
        catch (ViewBuildingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            Debug.WriteLine(ex.StackTrace);
            var message = CreateBuildErrorMessage(GetType(), ex);
            // A ViewBuildingException coming from a _set call is already recorded at its creation site
            // (handled by the catch above), so only the generic wrap is recorded here.
            Diagnostics.DiagnosticsErrorLog.Record(
                Diagnostics.DiagnosticSeverity.Error, Diagnostics.DiagnosticCategory.Build, DiagnosticSource, message);
            throw new ViewBuildingException(message, ex);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private static string CreateBuildErrorMessage(Type viewType, Exception exception)
    {
        var rootCause = GetRootCause(exception);
        var message = $"Build error in {viewType.Name} ({rootCause.GetType().Name}): {rootCause.Message}";

        if (IsBinaryCompatibilityException(rootCause))
        {
            message += " This usually means an external package was built against a different version of Avalonia or another dependency. Check the package's declared dependency versions against the versions resolved by the app.";
        }

        return message;
    }

    private static Exception GetRootCause(Exception exception)
    {
        while (exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private static bool IsBinaryCompatibilityException(Exception exception) =>
        exception is MissingFieldException or MissingMethodException or TypeLoadException or System.IO.FileLoadException;

    /// <summary>
    /// Gets the child control, ensuring initialization if needed.
    /// </summary>
    public new Control? Child
    {
        get
        {
            EnsureInitialized();
            return base.Child;
        }
        protected set => base.Child = value;
    }

    #region Hot reload stuff
    /// <summary>
    /// Reloads this view on the UI thread and returns a task that completes when the rebuild is done.
    /// </summary>
    /// <remarks>
    /// The returned task does not fault: exceptions are recorded to <see cref="Diagnostics.DiagnosticsErrorLog"/>
    /// instead, so callers may safely ignore it. The re-entrancy flags are owned by
    /// <see cref="InitializeCore"/> and this method; a reload never clears <c>_isInitializing</c>
    /// behind an in-flight build.
    /// </remarks>
    public Task ReloadAsync()
    {
        return ReloadCoreAsync();
    }

    private async Task ReloadCoreAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // InitializeCore owns the flags during a build; do not interleave with it.
                if (_isInitializing)
                    return;

                OnBeforeReload();
                SetValue(ChildProperty, null);
                VisualChildren.Clear();

                // BuildStyles() runs again in Initialize(); drop only the styles this view
                // contributed so externally added styles survive and nothing accumulates.
                if (_viewStyles is not null)
                {
                    foreach (var style in _viewStyles)
                        Styles.Remove(style);
                    _viewStyles = null;
                }

                _nameScope = null;
                _isInitialized = false;

                InitializeCore(force: true);

                InvalidateArrange();
                InvalidateMeasure();
                InvalidateVisual();
            });
        }
        catch (Exception ex)
        {
            Diagnostics.DiagnosticsErrorLog.Record(
                Diagnostics.DiagnosticSeverity.Error,
                Diagnostics.DiagnosticCategory.Runtime,
                DiagnosticSource,
                $"Reload failed in {GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Forces a reload of this view. Kept for <see cref="IReloadable"/> compatibility;
    /// use <see cref="ReloadAsync"/> when completion or failures need to be observed by the caller.
    /// </summary>
    public void Reload()
    {
        // ReloadAsync never faults, but observe the task defensively so that no exception
        // can go unobserved through this fire-and-forget entry point.
        ObserveFaults(ReloadAsync());
    }

    private static void ObserveFaults(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                _ = task.Exception;

            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    protected virtual void OnBeforeReload()
    {
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HotReloadManager.RegisterInstance(this);
        EnsureInitialized();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HotReloadManager.UnregisterInstance(this);
    }
    #endregion
}

public class ViewBuildingException(string message, Exception innerException) : Exception(message, innerException);
