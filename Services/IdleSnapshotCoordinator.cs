using System.Diagnostics;
using IsraeliAuthorStudio.Models;

namespace IsraeliAuthorStudio.Services;

public sealed class IdleSnapshotOptions
{
    public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int MetadataBatchSize { get; set; } = 10;
    public int MetadataConcurrency { get; set; } = 2;
}

public sealed class SyncStatusService
{
    private readonly object _gate = new();
    private SyncResult _lastResult = new(SyncState.UpToDate, "נשמר מקומית");
    private DateTimeOffset? _nextRetryAt;
    private int _failureCount;

    public event Action? Changed;

    public SyncResult LastResult { get { lock (_gate) return _lastResult; } }
    public DateTimeOffset? NextRetryAt { get { lock (_gate) return _nextRetryAt; } }
    public bool IsPaused => LastResult.State == SyncState.Conflict;

    public void Set(SyncResult result)
    {
        lock (_gate)
        {
            _lastResult = result;
            if (result.IsSuccess)
            {
                _failureCount = 0;
                _nextRetryAt = null;
            }
            else if (result.State != SyncState.Conflict)
            {
                _failureCount = Math.Min(_failureCount + 1, 6);
                _nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, _failureCount - 1));
            }
        }
        Changed?.Invoke();
    }

    public void ClearPause()
    {
        lock (_gate)
        {
            _lastResult = new SyncResult(SyncState.UpToDate, "מנסה לסנכרן מחדש");
            _nextRetryAt = null;
        }
        Changed?.Invoke();
    }
}

public sealed class MetadataBatchProcessor
{
    private readonly SceneMetadataRepository _metadata;
    private readonly StoryRepository _repository;
    private readonly MetadataAnalysisService _analysis;
    private readonly AssistantSettingsService _settings;
    private readonly ProjectActivityTracker _activity;
    private readonly IdleSnapshotOptions _options;

    public MetadataBatchProcessor(
        SceneMetadataRepository metadata,
        StoryRepository repository,
        MetadataAnalysisService analysis,
        AssistantSettingsService settings,
        ProjectActivityTracker activity,
        IdleSnapshotOptions options)
    {
        _metadata = metadata;
        _repository = repository;
        _analysis = analysis;
        _settings = settings;
        _activity = activity;
        _options = options;
    }

    public async Task<int> ProcessAsync(StoryWorkspace workspace, long sourceGeneration, CancellationToken cancellationToken)
    {
        var staleIds = await _metadata.GetStaleSceneIdsAsync(workspace.Scenes, _options.MetadataBatchSize, cancellationToken);
        if (staleIds.Count == 0) return 0;
        var settings = await _settings.LoadAsync(cancellationToken);
        if (!settings.Provider.IsConfigured) return 0;
        var metadataWorkspace = await _metadata.LoadWorkspaceAsync(cancellationToken);
        var semaphore = new SemaphoreSlim(Math.Max(1, _options.MetadataConcurrency));
        var tasks = staleIds.Select(async sceneId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var scene = workspace.Scenes.First(item => item.Id == sceneId);
                var hash = SceneMetadataRepository.ComputeContentHash(scene.Content);
                var context = BuildTimelineContext(sceneId, workspace, metadataWorkspace.Timeline);
                var result = await _analysis.AnalyzeAsync(scene, metadataWorkspace, context, cancellationToken);
                return new PendingMetadataResult(scene, hash, result);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var applied = 0;
        foreach (var pending in results)
        {
            if (!_activity.IsCurrent(sourceGeneration)) break;
            if (pending.Result is null) continue;
            if (await _repository.ApplySceneAnalysisAsync(
                    pending.Scene.Id,
                    pending.ContentHash,
                    pending.Result,
                    settings.Provider.MetadataModel))
            {
                applied++;
            }
        }
        return applied;
    }

    public async Task<MetadataAnalysisRunResult> AnalyzeSceneNowAsync(
        string sceneId,
        CancellationToken cancellationToken = default)
    {
        var totalTimer = Stopwatch.StartNew();
        var aiDuration = TimeSpan.Zero;
        var indexUpdateDuration = TimeSpan.Zero;

        MetadataAnalysisRunResult Complete(MetadataAnalysisRunState state, string message)
        {
            totalTimer.Stop();
            var preparationDuration = totalTimer.Elapsed - aiDuration - indexUpdateDuration;
            if (preparationDuration < TimeSpan.Zero) preparationDuration = TimeSpan.Zero;
            return new MetadataAnalysisRunResult(
                state,
                message,
                preparationDuration,
                aiDuration,
                indexUpdateDuration,
                totalTimer.Elapsed);
        }

        var settings = await _settings.LoadAsync(cancellationToken);
        if (!settings.Provider.IsConfigured)
        {
            return Complete(
                MetadataAnalysisRunState.NotConfigured,
                "יש להגדיר ספק ומודלים בהגדרות עוזר הכתיבה.");
        }

        var apiKey = await _settings.GetApiKeyAsync(settings.Provider, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Complete(
                MetadataAnalysisRunState.NotConfigured,
                "יש להגדיר מפתח API בהגדרות עוזר הכתיבה.");
        }

        var workspace = await _repository.LoadWorkspaceAsync();
        var scene = workspace.Scenes.FirstOrDefault(item => item.Id == sceneId);
        if (scene is null)
        {
            return Complete(
                MetadataAnalysisRunState.SceneNotFound,
                "הסצנה כבר אינה קיימת.");
        }

        var hash = SceneMetadataRepository.ComputeContentHash(scene.Content);
        var metadataWorkspace = await _metadata.LoadWorkspaceAsync(cancellationToken);
        var context = BuildTimelineContext(sceneId, workspace, metadataWorkspace.Timeline);

        MetadataAnalysisResult? analysis;
        var analysisFailed = false;
        var aiTimer = Stopwatch.StartNew();
        try
        {
            analysis = await _analysis.AnalyzeAsync(scene, metadataWorkspace, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            analysis = null;
            analysisFailed = true;
        }
        finally
        {
            aiTimer.Stop();
            aiDuration = aiTimer.Elapsed;
        }

        if (analysisFailed)
        {
            return Complete(
                MetadataAnalysisRunState.Failed,
                "הניתוח נכשל. בדקו את החיבור ואת הגדרות המודל.");
        }

        if (analysis is null)
        {
            return Complete(
                MetadataAnalysisRunState.Failed,
                "המודל לא החזיר נתונים תקינים לסצנה.");
        }

        bool applied;
        var indexTimer = Stopwatch.StartNew();
        try
        {
            applied = await _repository.ApplySceneAnalysisAsync(
                scene.Id,
                hash,
                analysis,
                settings.Provider.MetadataModel,
                replaceLockedFields: true);
        }
        finally
        {
            indexTimer.Stop();
            indexUpdateDuration = indexTimer.Elapsed;
        }

        return applied
            ? Complete(
                MetadataAnalysisRunState.Applied,
                "הניתוח הושלם ונתוני הסצנה עודכנו.")
            : Complete(
                MetadataAnalysisRunState.Stale,
                "הסצנה השתנתה בזמן הניתוח. לחצו שוב כדי לנתח את הגרסה החדשה.");
    }

    private static string BuildTimelineContext(string sceneId, StoryWorkspace workspace, TimelineIndexDocument timeline)
    {
        var ordered = timeline.Entries.Select(entry => entry.SceneId).ToList();
        var index = ordered.IndexOf(sceneId);
        var nearby = index < 0
            ? workspace.Scenes.Take(8).Select(scene => scene.Id)
            : ordered.Skip(Math.Max(0, index - 4)).Take(9);
        return string.Join(", ", nearby.Select(id =>
        {
            var scene = workspace.Scenes.FirstOrDefault(item => item.Id == id);
            var label = timeline.Entries.FirstOrDefault(entry => entry.SceneId == id)?.Label;
            return $"{id}:{scene?.Chapter}:{label}";
        }));
    }

    private sealed record PendingMetadataResult(SceneDocument Scene, string ContentHash, MetadataAnalysisResult? Result);
}

public enum MetadataAnalysisRunState
{
    Applied,
    NotConfigured,
    SceneNotFound,
    Stale,
    Failed
}

public sealed record MetadataAnalysisRunResult(
    MetadataAnalysisRunState State,
    string Message,
    TimeSpan PreparationDuration = default,
    TimeSpan AiDuration = default,
    TimeSpan IndexUpdateDuration = default,
    TimeSpan BackendDuration = default)
{
    public bool Success => State == MetadataAnalysisRunState.Applied;
}

public sealed class IdleSnapshotCoordinator : BackgroundService
{
    private readonly StoryRepository _repository;
    private readonly ProjectSelectionService _projects;
    private readonly ProjectActivityTracker _activity;
    private readonly MetadataBatchProcessor _metadata;
    private readonly GitRepositoryService _git;
    private readonly SyncStatusService _status;
    private readonly IdleSnapshotOptions _options;
    private readonly ProjectOperationCoordinator _operations;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private long _lastCompletedGeneration = -1;
    private int _forceRequested;

    public IdleSnapshotCoordinator(
        StoryRepository repository,
        ProjectSelectionService projects,
        ProjectActivityTracker activity,
        MetadataBatchProcessor metadata,
        GitRepositoryService git,
        SyncStatusService status,
        IdleSnapshotOptions options,
        ProjectOperationCoordinator operations)
    {
        _repository = repository;
        _projects = projects;
        _activity = activity;
        _metadata = metadata;
        _git = git;
        _status = status;
        _options = options;
        _operations = operations;
    }

    public void RequestSnapshot() => Interlocked.Exchange(ref _forceRequested, 1);

    public async Task RetryNowAsync(CancellationToken cancellationToken = default)
    {
        _status.ClearPause();
        await RunCycleAsync(force: true, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var forced = Interlocked.Exchange(ref _forceRequested, 0) == 1;
                var idle = DateTimeOffset.UtcNow - _activity.LastMutationAt >= _options.IdleInterval;
                var retryDue = _status.NextRetryAt is { } retry && retry <= DateTimeOffset.UtcNow;
                if (forced || retryDue || (idle && _activity.Generation != _lastCompletedGeneration && !_status.IsPaused))
                {
                    await RunCycleAsync(forced, stoppingToken);
                }
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _status.Set(new SyncResult(SyncState.Failed, exception.Message));
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await RunCycleAsync(force: true, cancellationToken); }
        catch (OperationCanceledException) { }
        await base.StopAsync(cancellationToken);
    }

    private async Task RunCycleAsync(bool force, CancellationToken cancellationToken)
    {
        if (!await _cycleGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var projectPath = _projects.CurrentProjectPath;
            if (!Directory.Exists(Path.Combine(projectPath, "Scenes"))) return;
            var generation = _activity.CaptureGeneration();
            var workspace = await _repository.LoadWorkspaceAsync();
            if (!_activity.IsCurrent(generation) && !force) return;
            if (!_status.IsPaused) await _metadata.ProcessAsync(workspace, generation, cancellationToken);
            var result = await _operations.RunAsync(
                () => _git.SnapshotAndSyncAsync(projectPath, $"autosave: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}", cancellationToken),
                cancellationToken);
            _status.Set(result);
            if (result.IsSuccess) _lastCompletedGeneration = _activity.Generation;
        }
        finally
        {
            _cycleGate.Release();
        }
    }
}
