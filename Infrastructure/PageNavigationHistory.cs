namespace WinOptimizer.Infrastructure;

/// <summary>
/// Ограниченная история переходов между разделами с отдельными стеками «назад» и «вперёд».
/// Недоступные пользователю страницы пропускаются без попадания в противоположный стек.
/// </summary>
internal sealed class PageNavigationHistory<T> where T : notnull
{
    private const int MaxEntries = 64;
    private readonly Stack<T> _back = new();
    private readonly Stack<T> _forward = new();

    public void Record(T current, T next)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return;

        _back.Push(current);
        if (_back.Count > MaxEntries)
        {
            var retained = _back.Take(MaxEntries).Reverse().ToArray();
            _back.Clear();
            foreach (var item in retained) _back.Push(item);
        }

        _forward.Clear();
    }

    public bool TryBack(T current, Func<T, bool> canNavigate, out T target) =>
        TryMove(_back, _forward, current, canNavigate, out target);

    public bool TryForward(T current, Func<T, bool> canNavigate, out T target) =>
        TryMove(_forward, _back, current, canNavigate, out target);

    private static bool TryMove(
        Stack<T> source,
        Stack<T> destination,
        T current,
        Func<T, bool> canNavigate,
        out T target)
    {
        ArgumentNullException.ThrowIfNull(canNavigate);

        while (source.Count > 0)
        {
            var candidate = source.Pop();
            if (!canNavigate(candidate)) continue;

            destination.Push(current);
            target = candidate;
            return true;
        }

        target = default!;
        return false;
    }
}
