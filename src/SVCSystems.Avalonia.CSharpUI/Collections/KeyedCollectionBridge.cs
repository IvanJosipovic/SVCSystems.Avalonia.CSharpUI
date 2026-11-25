using System.Collections.ObjectModel;

namespace SVCSystems.Avalonia.CSharpUI.Collections;

/// <summary>
/// Applies an immutable list to an <see cref="ObservableCollection{T}"/> using key identity. The
/// target keeps instance identity for surviving items, so a virtualizing control sees a real
/// <c>Move</c> rather than a remove/add pair and can animate or recycle correctly.
/// </summary>
/// <typeparam name="T">Item type. Use a reference type so identity survives a move.</typeparam>
public sealed class KeyedCollectionBridge<T>
    where T : class
{
    private readonly ObservableCollection<T> _target;
    private readonly Func<T, string> _keySelector;

    public KeyedCollectionBridge(ObservableCollection<T> target, Func<T, string> keySelector)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
    }

    public ObservableCollection<T> Target => _target;

    /// <summary>
    /// Reconciles <paramref name="items"/> into the target. Duplicate keys in the input are rejected
    /// before any mutation; duplicate keys already present in the target are collapsed during
    /// reconciliation so one desired row leaves exactly one target row.
    /// </summary>
    public void Apply(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var newKeys = new string[items.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var key = _keySelector(items[i]);
            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"Duplicate key '{key}' at index {i}.");
            }

            newKeys[i] = key;
        }

        // Fast path: an empty replacement.
        if (items.Count == 0)
        {
            _target.Clear();
            return;
        }

        // Remove items whose keys are gone and surplus occurrences of keys already present in the
        // target. Walking backwards keeps indices valid; the first occurrence of each key is kept so
        // unique surviving rows retain their instance identity.
        var desired = new HashSet<string>(newKeys, StringComparer.Ordinal);
        var firstIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _target.Count; i++)
        {
            firstIndexByKey.TryAdd(_keySelector(_target[i]), i);
        }

        for (var i = _target.Count - 1; i >= 0; i--)
        {
            var key = _keySelector(_target[i]);
            if (!desired.Contains(key) || firstIndexByKey[key] != i)
            {
                _target.RemoveAt(i);
            }
        }

        // Position survivors and insert new items in their final order. Move is used instead of
        // remove/insert so consumers receive a single Move notification for a reordered row.
        for (var i = 0; i < items.Count; i++)
        {
            if (i < _target.Count && string.Equals(_keySelector(_target[i]), newKeys[i], StringComparison.Ordinal))
            {
                continue;
            }

            var existing = -1;
            for (var j = 0; j < _target.Count; j++)
            {
                if (string.Equals(_keySelector(_target[j]), newKeys[i], StringComparison.Ordinal))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                if (existing != i)
                {
                    _target.Move(existing, i);
                }
            }
            else
            {
                _target.Insert(i, items[i]);
            }
        }
    }
}
