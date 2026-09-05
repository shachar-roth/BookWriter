namespace IsraeliAuthorStudio.Services;

public sealed class ProjectOperationCoordinator
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            Gate.Release();
        }
    }
}
