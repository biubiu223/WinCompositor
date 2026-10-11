namespace Compositor.App.Services;

/// <summary>
/// Lets a model go once it hasn't been used for a while: its session (and the memory, or graphics memory, it holds)
/// is released, and the next use loads it again. Every model the app runs has one, so an idle editor holds no AI
/// models at all.
/// </summary>
public sealed class ModelIdle : IDisposable
{
    private static readonly List<WeakReference<ModelIdle>> All = new();
    private readonly Action _release;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;

    public ModelIdle(Action release, TimeSpan delay)
    {
        _release = release;
        _delay = delay;
        _timer = new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
        lock (All) All.Add(new WeakReference<ModelIdle>(this));
    }

    /// <summary>Just used: the countdown starts over.</summary>
    public void Touch() => _timer.Change(_delay, Timeout.InfiniteTimeSpan);

    private void Release()
    {
        try { _release(); }
        catch { /* a model mid-use releases on its next idle */ }
        GC.Collect();
    }

    /// <summary>Releases every model now (the app is closing).</summary>
    public static void ReleaseAll()
    {
        lock (All)
            foreach (var reference in All)
                if (reference.TryGetTarget(out var idle)) { idle._timer.Change(Timeout.Infinite, Timeout.Infinite); idle.Release(); }
    }

    public void Dispose() => _timer.Dispose();
}
