using System.Text.Json.Serialization;

namespace IsraeliAuthorStudio.Models;

public sealed class ProviderProfile
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ChatModel { get; set; } = "gpt-5.6-terra";
    public string MetadataModel { get; set; } = "gpt-5.6-luna";
    public string MetadataReasoningEffort { get; set; } = "none";
    public string CredentialName { get; set; } = "israeli-author-studio:llm";
    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out _) &&
                                !string.IsNullOrWhiteSpace(ChatModel) &&
                                !string.IsNullOrWhiteSpace(MetadataModel);
}

public sealed class AssistantSettings
{
    public ProviderProfile Provider { get; set; } = new();
    public string GitAuthorName { get; set; } = "";
    public string GitAuthorEmail { get; set; } = "";
}

public enum AssistantMessageRole
{
    User,
    Assistant,
    System
}

public sealed class AssistantMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AssistantMessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AgentProposal? Proposal { get; set; }
}

public enum AgentOperationKind
{
    ReplaceSceneText,
    CreateScene,
    DeleteScene,
    SplitScene,
    JoinScenes,
    MoveScene,
    CreateChapter,
    RenameChapter,
    UpdateMetadata
}

public sealed class AgentProposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Summary { get; set; } = "";
    public List<AgentOperation> Operations { get; set; } = [];
    public bool IsApplied { get; set; }
    public bool IsRejected { get; set; }
    public bool IsStale { get; set; }
    public bool IsUndone { get; set; }
    public string? CommitHash { get; set; }
}

public sealed class AgentOperation
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentOperationKind Kind { get; set; }
    public string SceneId { get; set; } = "";
    public string? TargetSceneId { get; set; }
    public string? ChapterName { get; set; }
    public string? Content { get; set; }
    public int? SplitOffset { get; set; }
    public string ExpectedContentHash { get; set; } = "";
    public string PreviewBefore { get; set; } = "";
    public string PreviewAfter { get; set; } = "";
    public List<string> Characters { get; set; } = [];
    public List<string> Locations { get; set; } = [];
    public string TimeLabel { get; set; } = "";
}

public sealed class SceneMetadataDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string SceneId { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> CharacterIds { get; set; } = [];
    public List<string> LocationIds { get; set; } = [];
    public SceneTimeMetadata Time { get; set; } = new();
    public MetadataFieldLocks Locks { get; set; } = new();
    public string AnalyzedContentHash { get; set; } = "";
    public DateTimeOffset? AnalyzedAt { get; set; }
    public string AnalyzerModel { get; set; } = "";
}

public sealed class SceneTimeMetadata
{
    public string Label { get; set; } = "";
    public string? PlaceAfterSceneId { get; set; }
    public string Confidence { get; set; } = "unknown";
}

public sealed class MetadataFieldLocks
{
    public bool Summary { get; set; }
    public bool Characters { get; set; }
    public bool Locations { get; set; }
    public bool Time { get; set; }
}

public sealed class EntityIndexDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<EntityIndexEntry> Entities { get; set; } = [];
}

public sealed class EntityIndexEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> SceneIds { get; set; } = [];
}

public sealed class TimelineIndexDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<TimelineIndexEntry> Entries { get; set; } = [];
}

public sealed class TimelineIndexEntry
{
    public string SceneId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Confidence { get; set; } = "unknown";
}

public sealed class MetadataAnalysisResult
{
    public string Summary { get; set; } = "";
    public List<InferredEntity> Characters { get; set; } = [];
    public List<InferredEntity> Locations { get; set; } = [];
    public string TimeLabel { get; set; } = "";
    public string? PlaceAfterSceneId { get; set; }
    public string TimeConfidence { get; set; } = "unknown";
}

public sealed class InferredEntity
{
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
}

public sealed record GitOperationResult(bool Success, string Output = "", string Error = "", int ExitCode = 0)
{
    public static GitOperationResult Failed(string error, int exitCode = -1) => new(false, "", error, exitCode);
}

public enum SyncState
{
    UpToDate,
    CommittedLocally,
    Pushed,
    NoRemote,
    NeedsIdentity,
    AuthenticationFailed,
    Offline,
    Conflict,
    Failed
}

public sealed record SyncResult(SyncState State, string Message, string? CommitHash = null)
{
    public bool IsSuccess => State is SyncState.UpToDate or SyncState.CommittedLocally or SyncState.Pushed or SyncState.NoRemote;
}
