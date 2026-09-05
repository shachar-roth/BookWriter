namespace IsraeliAuthorStudio.Services;

public sealed class ProjectActivityTracker
{
    private long _generation;
    private long _lastMutationUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _activeEditors;

    public long Generation => Interlocked.Read(ref _generation);
    public DateTimeOffset LastMutationAt => new(Interlocked.Read(ref _lastMutationUtcTicks), TimeSpan.Zero);
    public int ActiveEditors => Volatile.Read(ref _activeEditors);

    public void MarkMutation()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _lastMutationUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public long CaptureGeneration() => Generation;
    public bool IsCurrent(long generation) => Generation == generation;
    public void RegisterEditor() => Interlocked.Increment(ref _activeEditors);
    public void UnregisterEditor() => Interlocked.Decrement(ref _activeEditors);
}
