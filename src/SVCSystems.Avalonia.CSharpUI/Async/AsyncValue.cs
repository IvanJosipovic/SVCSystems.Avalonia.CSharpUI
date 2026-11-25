namespace SVCSystems.Avalonia.CSharpUI.Async;

/// <summary>
/// Explicit state for an asynchronous value. Four states are used instead of three so that a
/// refresh with stale data is distinguishable from a first load: <see cref="Reloading"/> carries
/// the previous value and forces a switch expression to decide whether to show it.
/// </summary>
/// <remarks>
/// The hierarchy is nested and the constructor is <c>private protected</c> so consumers cannot add
/// states outside the library. Prefer a switch expression over <see cref="Match{TResult}"/> on hot
/// paths; the helper exists for concise view-model code.
/// </remarks>
public abstract record AsyncValue<T>
{
    private protected AsyncValue()
    {
    }

    /// <summary>First load; there is no value to display.</summary>
    public sealed record Loading : AsyncValue<T>;

    /// <summary>A value is available.</summary>
    public sealed record Data(T Value) : AsyncValue<T>;

    /// <summary>A refresh is in flight and the previous value is still available.</summary>
    public sealed record Reloading(T Previous) : AsyncValue<T>;

    /// <summary>The operation failed.</summary>
    public sealed record Error(Exception Exception) : AsyncValue<T>;

    /// <summary>Whether a value can be displayed, including stale-while-revalidate.</summary>
    public bool HasValue => this is Data or Reloading;

    public bool IsLoading => this is Loading or Reloading;

    public TResult Match<TResult>(
        Func<TResult> loading,
        Func<T, TResult> data,
        Func<T, TResult> reloading,
        Func<Exception, TResult> error) => this switch
    {
        Loading => loading(),
        Data value => data(value.Value),
        Reloading value => reloading(value.Previous),
        Error failure => error(failure.Exception),
        _ => throw new InvalidOperationException("Unknown asynchronous value state.")
    };

    /// <summary>Returns the current value, or <c>default</c> when none is available.</summary>
    public T? GetValueOrDefault() => this switch
    {
        Data value => value.Value,
        Reloading value => value.Previous,
        _ => default
    };
}
