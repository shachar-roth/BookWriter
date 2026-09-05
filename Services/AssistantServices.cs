using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsraeliAuthorStudio.Models;
using Microsoft.Extensions.AI;

namespace IsraeliAuthorStudio.Services;

public interface IAssistantClientFactory
{
    Task<IChatClient?> CreateAsync(bool useMetadataModel, CancellationToken cancellationToken = default);
}

public sealed class AssistantClientFactory : IAssistantClientFactory
{
    private readonly AssistantSettingsService _settings;

    public AssistantClientFactory(AssistantSettingsService settings)
    {
        _settings = settings;
    }

    public async Task<IChatClient?> CreateAsync(bool useMetadataModel, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        if (!settings.Provider.IsConfigured) return null;
        var key = await _settings.GetApiKeyAsync(settings.Provider, cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var model = useMetadataModel ? settings.Provider.MetadataModel : settings.Provider.ChatModel;
        var reasoningEffort = useMetadataModel ? settings.Provider.MetadataReasoningEffort : null;
        return new OpenAiCompatibleChatClient(
            new Uri(settings.Provider.Endpoint, UriKind.Absolute),
            model,
            key,
            reasoningEffort);
    }
}

public sealed class AssistantReadTools
{
    private readonly StoryRepository _repository;
    private readonly ProjectSelectionService _projects;

    public AssistantReadTools(StoryRepository repository, ProjectSelectionService projects)
    {
        _repository = repository;
        _projects = projects;
    }

    public async Task<StoryWorkspace> GetProjectOverviewAsync() => await _repository.LoadWorkspaceAsync();

    public async Task<SceneDocument?> GetSceneAsync(string sceneId) =>
        (await _repository.LoadWorkspaceAsync()).Scenes.FirstOrDefault(scene => scene.Id == sceneId);

    public async Task<IReadOnlyList<SceneDocument>> SearchScenesAsync(string query, int maximum = 8)
    {
        var workspace = await _repository.LoadWorkspaceAsync();
        var terms = Tokenize(query);
        if (terms.Count == 0) return workspace.Scenes.Take(maximum).ToList();
        return workspace.Scenes
            .Select(scene => (Scene: scene, Score: terms.Sum(term => CountOccurrences($"{scene.Summary}\n{scene.Content}", term))))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Scene.Order)
            .Take(maximum)
            .Select(item => item.Scene)
            .ToList();
    }

    public async Task<string> BuildContextAsync(string query, string? activeSceneId, CancellationToken cancellationToken = default)
    {
        var workspace = await _repository.LoadWorkspaceAsync();
        var selected = (await SearchScenesAsync(query, 6)).ToList();
        var active = workspace.Scenes.FirstOrDefault(scene => scene.Id == activeSceneId);
        if (active is not null && selected.All(scene => scene.Id != active.Id)) selected.Insert(0, active);
        selected = selected.DistinctBy(scene => scene.Id).Take(7).ToList();

        var memoryPath = Path.Combine(_projects.CurrentProjectPath, "Assistant", "project-memory.md");
        var memory = File.Exists(memoryPath) ? await File.ReadAllTextAsync(memoryPath, cancellationToken) : "";
        var builder = new StringBuilder();
        builder.AppendLine("PROJECT MEMORY:");
        builder.AppendLine(Truncate(memory, 6000));
        builder.AppendLine("CHAPTER AND SCENE MAP:");
        foreach (var chapter in workspace.ChaptersIndex.Chapters)
        {
            builder.AppendLine($"## {chapter.Name}");
            foreach (var sceneId in chapter.SceneIds)
            {
                var scene = workspace.Scenes.FirstOrDefault(item => item.Id == sceneId);
                if (scene is null) continue;
                var summary = workspace.SceneMetadata.GetValueOrDefault(scene.Id)?.Summary;
                builder.AppendLine($"- {scene.Id} [{SceneMetadataRepository.ComputeContentHash(scene.Content)}]: {Truncate(string.IsNullOrWhiteSpace(summary) ? SceneLabel(scene) : summary, 240)}");
            }
        }
        builder.AppendLine("RELEVANT FULL SCENES:");
        foreach (var scene in selected)
        {
            builder.AppendLine($"### {scene.Id} | {scene.Chapter} | hash={SceneMetadataRepository.ComputeContentHash(scene.Content)}");
            builder.AppendLine(Truncate(scene.Content, 9000));
        }
        return builder.ToString();
    }

    private static List<string> Tokenize(string value) =>
        Regex.Matches(value ?? "", @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    private static string Truncate(string? value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value ?? "" : $"{value[..maximum]}\n[...]";

    private static string SceneLabel(SceneDocument scene) =>
        scene.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "סצנה ריקה";
}

public sealed class AssistantConversationService
{
    private readonly IAssistantClientFactory _clients;
    private readonly AssistantReadTools _tools;

    public AssistantConversationService(IAssistantClientFactory clients, AssistantReadTools tools)
    {
        _clients = clients;
        _tools = tools;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<AssistantMessage> history,
        string prompt,
        string? activeSceneId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = await _clients.CreateAsync(useMetadataModel: false, cancellationToken);
        if (client is null)
        {
            yield return "כדי להתחיל שיחה יש להגדיר ספק, מודל ומפתח API בהגדרות העוזר.";
            yield break;
        }

        var context = await _tools.BuildContextAsync(prompt, activeSceneId, cancellationToken);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(context))
        };
        messages.AddRange(history.Where(message => message.Role is AssistantMessageRole.User or AssistantMessageRole.Assistant)
            .TakeLast(20)
            .Select(message => new ChatMessage(message.Role == AssistantMessageRole.User ? ChatRole.User : ChatRole.Assistant, message.Content)));
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        await foreach (var update in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text)) yield return update.Text;
        }
    }

    private static string BuildSystemPrompt(string context) => $$"""
        You are a Hebrew-first writing copilot for a book manuscript. Answer in the writer's language.
        Use only the supplied project context. State uncertainty instead of inventing story facts.
        You may suggest changes, but you never apply them. When a concrete project change is appropriate,
        append exactly one fenced block named agent-proposal containing valid JSON matching this shape:
        {"summary":"...","operations":[{"kind":"ReplaceSceneText","sceneId":"scn-...","content":"...","expectedContentHash":"..."}]}
        Supported kind values: ReplaceSceneText, CreateScene, DeleteScene, SplitScene, JoinScenes,
        MoveScene, CreateChapter, RenameChapter, UpdateMetadata. Include expectedContentHash for existing scenes.
        Keep the normal answer outside that block concise and useful.

        {{context}}
        """;
}

public static class AgentProposalParser
{
    private const string ProposalFence = "```agent-proposal";
    private static readonly Regex ProposalBlock = new("```agent-proposal\\s*(?<json>\\{.*?\\})\\s*```", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex RawProposalBlock = new("(?<json>\\{\\s*\"summary\"\\s*:.*\\})\\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex RawProposalStart = new("\\{\\s*\"summary\"\\s*:", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    public static (string DisplayText, AgentProposal? Proposal) Extract(string response)
    {
        var normalized = response ?? "";
        var match = ProposalBlock.Match(normalized);
        if (!match.Success) match = RawProposalBlock.Match(normalized);
        if (!match.Success) return (normalized.Trim(), null);
        try
        {
            var proposal = JsonSerializer.Deserialize<AgentProposal>(match.Groups["json"].Value, JsonOptions);
            var displayText = normalized.Remove(match.Index, match.Length).Trim();
            return (displayText, proposal is { Operations.Count: > 0 } ? proposal : null);
        }
        catch (JsonException)
        {
            return (normalized.Trim(), null);
        }
    }

    public static string GetStreamingDisplayText(string response)
    {
        var normalized = response ?? "";
        var proposalIndex = normalized.IndexOf(ProposalFence, StringComparison.Ordinal);
        if (proposalIndex >= 0) return normalized[..proposalIndex].TrimEnd();

        var rawProposalMatch = RawProposalStart.Match(normalized);
        if (rawProposalMatch.Success) return normalized[..rawProposalMatch.Index].TrimEnd();

        var maximumPartialLength = Math.Min(ProposalFence.Length - 1, normalized.Length);
        for (var length = maximumPartialLength; length > 0; length--)
        {
            if (normalized.EndsWith(ProposalFence[..length], StringComparison.Ordinal))
                return normalized[..^length].TrimEnd();
        }

        return normalized;
    }
}

public sealed class AgentProposalService
{
    private readonly StoryRepository _repository;
    private readonly ProjectSelectionService _projects;
    private readonly GitRepositoryService _git;
    private readonly ProjectOperationCoordinator _operations;

    public AgentProposalService(
        StoryRepository repository,
        ProjectSelectionService projects,
        GitRepositoryService git,
        ProjectOperationCoordinator operations)
    {
        _repository = repository;
        _projects = projects;
        _git = git;
        _operations = operations;
    }

    public async Task<AgentProposal> PrepareAsync(AgentProposal proposal)
    {
        var workspace = await _repository.LoadWorkspaceAsync();
        foreach (var operation in proposal.Operations)
        {
            var scene = workspace.Scenes.FirstOrDefault(item => item.Id == operation.SceneId);
            if (scene is not null)
            {
                operation.ExpectedContentHash = SceneMetadataRepository.ComputeContentHash(scene.Content);
                if (string.IsNullOrWhiteSpace(operation.PreviewBefore)) operation.PreviewBefore = Preview(scene.Content);
            }
            if (string.IsNullOrWhiteSpace(operation.PreviewAfter))
            {
                operation.PreviewAfter = operation.Kind switch
                {
                    AgentOperationKind.ReplaceSceneText or AgentOperationKind.CreateScene => Preview(operation.Content ?? ""),
                    AgentOperationKind.DeleteScene => "הסצנה תימחק",
                    AgentOperationKind.RenameChapter => operation.ChapterName ?? "",
                    AgentOperationKind.UpdateMetadata => $"דמויות: {string.Join(", ", operation.Characters)}\nמקומות: {string.Join(", ", operation.Locations)}\nזמן: {operation.TimeLabel}",
                    _ => "שינוי מבני בסיפור"
                };
            }
        }
        return proposal;
    }

    public async Task<AgentApplyResult> ApplyAsync(AgentProposal proposal, CancellationToken cancellationToken = default)
    {
        if (proposal.IsApplied || proposal.IsRejected) return new AgentApplyResult(false, "ההצעה כבר טופלה.");
        var workspace = await _repository.LoadWorkspaceAsync();
        foreach (var operation in proposal.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.ExpectedContentHash)) continue;
            var scene = workspace.Scenes.FirstOrDefault(item => item.Id == operation.SceneId);
            if (scene is null || !string.Equals(SceneMetadataRepository.ComputeContentHash(scene.Content), operation.ExpectedContentHash, StringComparison.Ordinal))
            {
                proposal.IsStale = true;
                return new AgentApplyResult(false, "הסיפור השתנה מאז יצירת ההצעה. יש לבקש הצעה חדשה.", IsStale: true);
            }
        }

        var projectPath = _projects.CurrentProjectPath;
        var beforeCommit = await _git.CommitAsync(projectPath, "Autosave before assistant change", cancellationToken);
        if (!beforeCommit.Success) return new AgentApplyResult(false, beforeCommit.Error);
        var backupRoot = Path.Combine(Path.GetTempPath(), $"IsraeliAuthorStudio-agent-{Guid.NewGuid():N}");
        try
        {
            SnapshotProject(projectPath, backupRoot);
            foreach (var operation in proposal.Operations)
            {
                await ApplyOperationAsync(operation, cancellationToken);
            }
            var commit = await _git.CommitAsync(projectPath, $"assistant: {SanitizeCommitMessage(proposal.Summary)}", cancellationToken);
            if (!commit.Success) throw new InvalidOperationException(commit.Error);
            var head = await _git.GetHeadAsync(projectPath, cancellationToken);
            proposal.CommitHash = head.Success ? head.Output.Trim() : null;
            proposal.IsApplied = true;
            return new AgentApplyResult(true, "השינוי הוחל ונשמר ב-Git.", proposal.CommitHash);
        }
        catch (Exception exception)
        {
            RestoreProject(projectPath, backupRoot);
            await _git.UnstageAsync(projectPath, cancellationToken);
            return new AgentApplyResult(false, $"השינוי לא הוחל: {exception.Message}");
        }
        finally
        {
            DeleteDirectory(backupRoot);
        }
    }

    public async Task<AgentApplyResult> UndoAsync(AgentProposal proposal, CancellationToken cancellationToken = default)
    {
        if (!proposal.IsApplied || proposal.IsUndone || string.IsNullOrWhiteSpace(proposal.CommitHash))
            return new AgentApplyResult(false, "אין שינוי שניתן לבטל.");

        var result = await _operations.RunAsync(
            () => _git.RevertPreservingLocalChangesAsync(_projects.CurrentProjectPath, proposal.CommitHash, cancellationToken),
            cancellationToken);
        if (!result.Success) return new AgentApplyResult(false, $"לא ניתן לבטל את השינוי. {result.Error}");

        proposal.IsUndone = true;
        return new AgentApplyResult(true, "השינוי בוטל ב-Git.");
    }

    private async Task ApplyOperationAsync(AgentOperation operation, CancellationToken cancellationToken)
    {
        var workspace = await _repository.LoadWorkspaceAsync();
        var scene = workspace.Scenes.FirstOrDefault(item => item.Id == operation.SceneId);
        switch (operation.Kind)
        {
            case AgentOperationKind.ReplaceSceneText:
                if (scene is null) throw new InvalidOperationException("Scene not found.");
                await _repository.SaveSceneContentAsync(scene.Id, operation.Content ?? "");
                break;
            case AgentOperationKind.CreateScene:
            {
                var created = !string.IsNullOrWhiteSpace(operation.TargetSceneId)
                    ? await _repository.CreateSceneAfterAsync(operation.TargetSceneId)
                    : await _repository.CreateSceneAsync();
                if (created is null) throw new InvalidOperationException("The target scene was not found.");
                if (!string.IsNullOrEmpty(operation.Content)) await _repository.SaveSceneContentAsync(created.Id, operation.Content);
                break;
            }
            case AgentOperationKind.DeleteScene:
                if (scene is null) throw new InvalidOperationException("Scene not found.");
                await _repository.DeleteSceneAsync(scene.Id);
                break;
            case AgentOperationKind.SplitScene:
                if (scene is null || operation.SplitOffset is null) throw new InvalidOperationException("A valid split position is required.");
                await _repository.SplitSceneAsync(scene, operation.SplitOffset.Value);
                break;
            case AgentOperationKind.JoinScenes:
                if (scene is null) throw new InvalidOperationException("Scene not found.");
                await _repository.JoinWithNextSceneAsync(scene);
                break;
            case AgentOperationKind.MoveScene:
                if (scene is null) throw new InvalidOperationException("Scene not found.");
                if (string.IsNullOrWhiteSpace(operation.TargetSceneId)) await _repository.MoveSceneToEndAsync(scene.Id);
                else await _repository.ReorderSceneBeforeAsync(scene.Id, operation.TargetSceneId);
                break;
            case AgentOperationKind.CreateChapter:
                if (!string.IsNullOrWhiteSpace(operation.SceneId)) await _repository.AddChapterDividerBeforeSceneAsync(operation.SceneId);
                else if (workspace.Scenes.LastOrDefault() is { } last) await _repository.CreateChapterAfterAsync(last.Id);
                break;
            case AgentOperationKind.RenameChapter:
                if (string.IsNullOrWhiteSpace(operation.SceneId) || string.IsNullOrWhiteSpace(operation.ChapterName))
                    throw new InvalidOperationException("A chapter and name are required.");
                await _repository.RenameChapterAsync(operation.SceneId, operation.ChapterName);
                break;
            case AgentOperationKind.UpdateMetadata:
                if (scene is null) throw new InvalidOperationException("Scene not found.");
                await _repository.SaveSceneMetadataAsync(scene.Id, operation.TimeLabel, operation.Locations);
                break;
            default:
                throw new InvalidOperationException($"Unsupported operation {operation.Kind}.");
        }
    }

    private static void SnapshotProject(string projectPath, string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);
        foreach (var name in new[] { "Scenes", "Indexes", "Metadata", "Assistant" })
        {
            var source = Path.Combine(projectPath, name);
            if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(backupRoot, name));
        }
    }

    private static void RestoreProject(string projectPath, string backupRoot)
    {
        foreach (var name in new[] { "Scenes", "Indexes", "Metadata", "Assistant" })
        {
            var target = Path.Combine(projectPath, name);
            DeleteDirectory(target);
            var source = Path.Combine(backupRoot, name);
            if (Directory.Exists(source)) CopyDirectory(source, target);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static string SanitizeCommitMessage(string value)
    {
        var singleLine = string.Join(' ', (value ?? "change").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= 120 ? singleLine : singleLine[..120];
    }

    private static string Preview(string value) => value.Length <= 700 ? value : $"{value[..700]}...";
}

public sealed record AgentApplyResult(bool Success, string Message, string? CommitHash = null, bool IsStale = false);

public sealed class MetadataAnalysisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly IAssistantClientFactory _clients;

    public MetadataAnalysisService(IAssistantClientFactory clients)
    {
        _clients = clients;
    }

    public async Task<MetadataAnalysisResult?> AnalyzeAsync(
        SceneDocument scene,
        MetadataWorkspace indexes,
        string context,
        CancellationToken cancellationToken = default)
    {
        using var client = await _clients.CreateAsync(useMetadataModel: true, cancellationToken);
        if (client is null) return null;
        var knownCharacters = string.Join(", ", indexes.Characters.Characters.Select(character => $"{character.Name} ({string.Join('|', character.Aliases)})"));
        var knownLocations = string.Join(", ", indexes.Locations.Entities.Select(location => $"{location.Name} ({string.Join('|', location.Aliases)})"));
        var prompt = $$"""
            Analyze this Hebrew story scene. Return JSON only with properties:
            summary, characters:[{name,aliases:[]}], locations:[{name,aliases:[]}],
            timeLabel, placeAfterSceneId, timeConfidence (high|medium|low|unknown).
            Reuse canonical names when appropriate. Do not infer an entity merely because it appears in instructions.
            Known characters: {{knownCharacters}}
            Known locations: {{knownLocations}}
            Nearby chronology: {{context}}
            Scene {{scene.Id}}:
            {{scene.Content}}
            """;
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: cancellationToken);
        return ParseJson<MetadataAnalysisResult>(response.Text);
    }

    internal static T? ParseJson<T>(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
        }
        try { return JsonSerializer.Deserialize<T>(trimmed, JsonOptions); }
        catch (JsonException) { return default; }
    }
}

public sealed class ProjectMemoryService
{
    private readonly IAssistantClientFactory _clients;
    private readonly ProjectSelectionService _projects;
    private readonly ProjectActivityTracker _activity;
    private readonly ProjectOperationCoordinator _operations;

    public ProjectMemoryService(
        IAssistantClientFactory clients,
        ProjectSelectionService projects,
        ProjectActivityTracker activity,
        ProjectOperationCoordinator operations)
    {
        _clients = clients;
        _projects = projects;
        _activity = activity;
        _operations = operations;
    }

    public async Task UpdateAsync(string userMessage, string assistantMessage, CancellationToken cancellationToken = default)
    {
        using var client = await _clients.CreateAsync(useMetadataModel: true, cancellationToken);
        if (client is null) return;
        var path = Path.Combine(_projects.CurrentProjectPath, "Assistant", "project-memory.md");
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "# Project Memory\n";
        var prompt = $$"""
            Rewrite the project memory below as concise Markdown. Keep durable story facts, writer preferences,
            decisions, and unresolved questions. Remove conversational wording and never include a transcript.
            Existing memory:
            {{current}}
            Latest writer message: {{userMessage}}
            Latest assistant response: {{assistantMessage}}
            """;
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: cancellationToken);
        await _operations.RunAsync(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(temporaryPath, response.Text.Trim(), new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            _activity.MarkMutation();
        }, cancellationToken);
    }
}
