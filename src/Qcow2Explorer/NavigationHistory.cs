namespace Qcow2Explorer;

public sealed class NavigationHistory<T> where T : class
{
    private const int MaximumEntries = 200;
    private readonly List<T> _entries = [];
    private int _index = -1;

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;
    public T? Current => _index >= 0 && _index < _entries.Count ? _entries[_index] : null;

    public void Record(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ReferenceEquals(Current, item))
        {
            return;
        }

        if (_index < _entries.Count - 1)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(item);
        _index = _entries.Count - 1;
        if (_entries.Count > MaximumEntries)
        {
            _entries.RemoveAt(0);
            _index--;
        }
    }

    public T? GoBack()
    {
        if (!CanGoBack)
        {
            return null;
        }

        _index--;
        return _entries[_index];
    }

    public T? GoForward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        _index++;
        return _entries[_index];
    }

    public void Reset()
    {
        _entries.Clear();
        _index = -1;
    }
}
