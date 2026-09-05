using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsraeliAuthorStudio.Models;

namespace IsraeliAuthorStudio.Services;

public sealed class StoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(5);
    private const int BackupsPerScene = 20;

    private readonly ProjectSelectionService _projectSelection;
    private readonly SemaphoreSlim _gate;
    private readonly ProjectActivityTracker? _activity;
    private readonly GitRepositoryService? _git;
    private readonly SceneMetadataRepository? _metadata;
    private readonly AssistantSettingsService? _assistantSettings;
    private readonly bool _integrationsEnabled;
    private readonly ILogger<StoryRepository>? _logger;

    public StoryRepository(ProjectSelectionService projectSelection)
    {
        _projectSelection = projectSelection;
        _gate = new SemaphoreSlim(1, 1);
    }

    public StoryRepository(
        ProjectSelectionService projectSelection,
        ProjectOperationCoordinator operations,
        ProjectActivityTracker activity,
        GitRepositoryService git,
        SceneMetadataRepository metadata,
        AssistantSettingsService assistantSettings,
        ILogger<StoryRepository>? logger = null)
    {
        _projectSelection = projectSelection;
        _gate = operations.Gate;
        _activity = activity;
        _git = git;
        _metadata = metadata;
        _assistantSettings = assistantSettings;
        _integrationsEnabled = true;
        _logger = logger;
    }

    private string StoryRoot => _projectSelection.CurrentProjectPath;
    private string ScenesRoot => Path.Combine(StoryRoot, "Scenes");
    private string IndexesRoot => Path.Combine(StoryRoot, "Indexes");
    private string HistoryRoot => Path.Combine(StoryRoot, ".history");
    private string ChaptersPath => Path.Combine(IndexesRoot, "chapters.json");
    private string CharactersPath => Path.Combine(IndexesRoot, "characters.json");
    private string CharacterNamesPath => Path.Combine(IndexesRoot, "character-names.txt");

    public async Task<StoryWorkspace> LoadWorkspaceAsync()
    {
        StoryWorkspace workspace;
        await _gate.WaitAsync();
        try
        {
            workspace = await LoadWorkspaceCoreAsync();
        }
        finally
        {
            _gate.Release();
        }

        if (!_integrationsEnabled || _git is null || _metadata is null || _assistantSettings is null)
        {
            return workspace;
        }

        var settings = await _assistantSettings.LoadAsync();
        var repository = await _git.EnsureProjectRepositoryAsync(
            StoryRoot,
            settings.GitAuthorName,
            settings.GitAuthorEmail);
        if (repository.Success)
        {
            var migrated = await _metadata.EnsureMigratedAsync(workspace.Scenes, workspace.CharactersIndex);
            if (migrated)
            {
                await _git.CommitAsync(StoryRoot, "Migrate scene metadata");
            }
        }
        else
        {
            _logger?.LogWarning("Git setup failed; existing scene metadata remains available. Reason: {Reason}", repository.Error);
        }

        // Reading existing metadata does not require Git. Only migration needs a baseline commit.
        var metadataWorkspace = await _metadata.LoadWorkspaceAsync();
        workspace.SceneMetadata = metadataWorkspace.SceneMetadata;
        if (_metadata.IsInitialized)
        {
            workspace.CharactersIndex = metadataWorkspace.Characters;
            workspace.LocationsIndex = metadataWorkspace.Locations;
            workspace.TimelineIndex = metadataWorkspace.Timeline;
            ApplyMetadataToScenes(workspace);
        }
        return workspace;
    }

    public async Task StartNewProjectAsync(string projectPath)
    {
        var scenesPath = Path.Combine(projectPath, "Scenes");
        var indexesPath = Path.Combine(projectPath, "Indexes");
        if ((Directory.Exists(scenesPath) && Directory.EnumerateFiles(scenesPath).Any()) ||
            (Directory.Exists(indexesPath) && Directory.EnumerateFiles(indexesPath).Any()))
        {
            throw new InvalidOperationException("The selected folder already contains a story project.");
        }

        await _projectSelection.SetCurrentProjectPathAsync(projectPath);
        await StartNewProjectAsync();
        if (_integrationsEnabled) _ = await LoadWorkspaceAsync();
    }

    public async Task StartNewProjectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureFolders();
            if (Directory.EnumerateFiles(ScenesRoot).Any() || Directory.EnumerateFiles(IndexesRoot).Any())
            {
                throw new InvalidOperationException("The selected folder already contains a story project.");
            }

            await EnsureSeedSceneCoreAsync();
            await LoadWorkspaceCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> OpenExistingProjectAsync(string projectPath)
    {
        if (!Directory.Exists(projectPath) || !Directory.Exists(Path.Combine(projectPath, "Scenes")))
        {
            return false;
        }

        await _projectSelection.SetCurrentProjectPathAsync(projectPath);
        await LoadWorkspaceAsync();
        return true;
    }

    public async Task<SceneDocument> CreateSceneAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var chapter = state.Chapters.Chapters.LastOrDefault();
            if (chapter is null)
            {
                chapter = new ChapterIndexEntry { Name = "פרק 1" };
                state.Chapters.Chapters.Add(chapter);
            }

            var scene = CreateBlankScene(chapter.Name);
            await WriteSceneMarkdownCoreAsync(scene);
            chapter.SceneIds.Add(scene.Id);
            await PersistStructureCoreAsync(state.Scenes.Append(scene).ToList(), state.Chapters);
            return scene;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<SceneDocument?> CreateSceneBeforeAsync(string targetSceneId) =>
        CreateSceneRelativeAsync(targetSceneId, insertAfter: false);

    public Task<SceneDocument?> CreateSceneAfterAsync(string targetSceneId) =>
        CreateSceneRelativeAsync(targetSceneId, insertAfter: true);

    public async Task<SceneDocument?> CreateChapterAfterAsync(string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var chapterIndex = state.Chapters.Chapters.FindIndex(item => item.SceneIds.Contains(sceneId, StringComparer.Ordinal));
            if (chapterIndex < 0)
            {
                return null;
            }

            var chapter = new ChapterIndexEntry { Name = CreateNextChapterName(state.Chapters) };
            var scene = CreateBlankScene(chapter.Name);
            chapter.SceneIds.Add(scene.Id);
            state.Chapters.Chapters.Insert(chapterIndex + 1, chapter);
            state.Scenes.Add(scene);
            await WriteSceneMarkdownCoreAsync(scene);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
            return scene;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SceneDocument?> CreateSceneRelativeAsync(string targetSceneId, bool insertAfter)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var chapter = state.Chapters.Chapters.FirstOrDefault(item => item.SceneIds.Contains(targetSceneId, StringComparer.Ordinal));
            if (chapter is null)
            {
                return null;
            }

            var targetIndex = chapter.SceneIds.IndexOf(targetSceneId);
            var scene = CreateBlankScene(chapter.Name);
            await WriteSceneMarkdownCoreAsync(scene);
            chapter.SceneIds.Insert(targetIndex + (insertAfter ? 1 : 0), scene.Id);
            await PersistStructureCoreAsync(state.Scenes.Append(scene).ToList(), state.Chapters);
            return scene;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ImportDocumentAsync(DocxImportDocument import)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            foreach (var importedChapter in import.Chapters.Where(chapter => chapter.Scenes.Count > 0))
            {
                var chapterName = EnsureUniqueChapterName(state.Chapters, importedChapter.Name);
                var chapter = new ChapterIndexEntry { Name = chapterName };
                state.Chapters.Chapters.Add(chapter);

                foreach (var importedScene in importedChapter.Scenes.Where(scene => !string.IsNullOrWhiteSpace(scene.Content)))
                {
                    var scene = CreateBlankScene(chapterName);
                    scene.Title = string.IsNullOrWhiteSpace(importedScene.Title) ? "סצנה מיובאת" : importedScene.Title.Trim();
                    scene.Content = importedScene.Content.Trim();
                    await WriteSceneMarkdownCoreAsync(scene);
                    chapter.SceneIds.Add(scene.Id);
                    state.Scenes.Add(scene);
                }
            }

            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SceneDocument> CreateSceneFromImportAsync(string title, string content)
    {
        var import = new DocxImportDocument
        {
            Chapters =
            [
                new DocxImportChapter
                {
                    Name = "ייבוא DOCX",
                    Scenes = [new DocxImportScene { Title = title, Content = content }]
                }
            ]
        };
        await ImportDocumentAsync(import);
        return (await LoadWorkspaceAsync()).Scenes.Last();
    }

    public async Task SaveSceneContentAsync(string sceneId, string content)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var scene = state.Scenes.FirstOrDefault(item => item.Id == sceneId);
            if (scene is null)
            {
                return;
            }

            scene.Content = content;
            scene.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteSceneMarkdownCoreAsync(scene, createBackup: true);
            if (_metadata?.IsInitialized != true) await WriteCharactersIndexCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSceneMetadataAsync(string sceneId, string timeline, IEnumerable<string> places)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var scene = state.Scenes.FirstOrDefault(item => item.Id == sceneId);
            if (scene is null)
            {
                return;
            }

            scene.Timeline = timeline.Trim();
            scene.Places = places.Select(place => place.Trim())
                .Where(place => place.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            scene.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteSceneMarkdownCoreAsync(scene, createBackup: true);
            if (_metadata?.IsInitialized == true)
            {
                await _metadata.SaveManualAsync(scene.Id, scene.Timeline, scene.Places, state.Scenes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSceneCharactersAsync(string sceneId, IEnumerable<string> characterNames)
    {
        if (_metadata is null) return;
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            await _metadata.SaveManualCharactersAsync(sceneId, characterNames, state.Scenes);
            _activity?.MarkMutation();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnlockSceneMetadataAsync(string sceneId)
    {
        if (_metadata is null) return;
        await _gate.WaitAsync();
        try
        {
            await _metadata.UnlockAsync(sceneId);
            _activity?.MarkMutation();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ApplySceneAnalysisAsync(
        string sceneId,
        string expectedContentHash,
        MetadataAnalysisResult analysis,
        string model,
        bool replaceLockedFields = false)
    {
        if (_metadata is null) return false;
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var scene = state.Scenes.FirstOrDefault(item => item.Id == sceneId);
            if (scene is null || SceneMetadataRepository.ComputeContentHash(scene.Content) != expectedContentHash) return false;
            var applied = await _metadata.ApplyAnalysisAsync(
                scene,
                expectedContentHash,
                analysis,
                model,
                state.Scenes,
                replaceLockedFields);
            if (applied) _activity?.MarkMutation();
            return applied;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSceneAsync(SceneDocument scene)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var existing = state.Scenes.FirstOrDefault(item => item.Id == scene.Id);
            if (existing is null)
            {
                return;
            }

            existing.Title = string.IsNullOrWhiteSpace(scene.Title) ? "סצנה ללא שם" : scene.Title.Trim();
            existing.Summary = scene.Summary;
            existing.Content = scene.Content;
            existing.Timeline = scene.Timeline.Trim();
            existing.Places = [.. scene.Places];
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteSceneMarkdownCoreAsync(existing, createBackup: true);
            if (_metadata?.IsInitialized != true) await WriteCharactersIndexCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSceneAsync(string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var scene = state.Scenes.FirstOrDefault(item => item.Id == sceneId);
            if (scene is null)
            {
                return;
            }

            File.Delete(GetScenePath(sceneId));
            state.Scenes.Remove(scene);
            RemoveSceneFromChapters(state.Chapters, sceneId);
            if (state.Scenes.Count == 0)
            {
                var seed = CreateBlankScene("פרק 1");
                await WriteSceneMarkdownCoreAsync(seed);
                state.Scenes.Add(seed);
                state.Chapters.Chapters.Add(new ChapterIndexEntry { Name = "פרק 1", SceneIds = [seed.Id] });
            }

            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task JoinWithNextSceneAsync(SceneDocument currentScene)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var index = state.Scenes.FindIndex(scene => scene.Id == currentScene.Id);
            if (index < 0 || index >= state.Scenes.Count - 1)
            {
                return;
            }

            var current = state.Scenes[index];
            var next = state.Scenes[index + 1];
            current.Content = JoinSceneContent(currentScene.Content, next.Content);
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteSceneMarkdownCoreAsync(current, createBackup: true);
            File.Delete(GetScenePath(next.Id));
            state.Scenes.RemoveAt(index + 1);
            RemoveSceneFromChapters(state.Chapters, next.Id);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SplitSceneAsync(SceneDocument currentScene, int splitOffset)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var current = state.Scenes.FirstOrDefault(scene => scene.Id == currentScene.Id);
            var chapter = state.Chapters.Chapters.FirstOrDefault(item => item.SceneIds.Contains(currentScene.Id, StringComparer.Ordinal));
            if (current is null || chapter is null)
            {
                return;
            }

            var content = currentScene.Content ?? "";
            splitOffset = Math.Clamp(splitOffset, 0, content.Length);
            current.Content = content[..splitOffset].TrimEnd('\r', '\n');
            current.UpdatedAt = DateTimeOffset.UtcNow;

            var newScene = CreateBlankScene(chapter.Name);
            newScene.Content = content[splitOffset..].TrimStart('\r', '\n');
            await WriteSceneMarkdownCoreAsync(current, createBackup: true);
            await WriteSceneMarkdownCoreAsync(newScene);

            var chapterIndex = chapter.SceneIds.IndexOf(current.Id);
            chapter.SceneIds.Insert(chapterIndex + 1, newScene.Id);
            state.Scenes.Add(newScene);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReorderSceneBeforeAsync(string draggedSceneId, string targetSceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            if (draggedSceneId == targetSceneId || state.Scenes.All(scene => scene.Id != draggedSceneId))
            {
                return;
            }

            RemoveSceneFromChapters(state.Chapters, draggedSceneId);
            var targetChapter = state.Chapters.Chapters.FirstOrDefault(item => item.SceneIds.Contains(targetSceneId, StringComparer.Ordinal));
            if (targetChapter is null)
            {
                return;
            }

            targetChapter.SceneIds.Insert(targetChapter.SceneIds.IndexOf(targetSceneId), draggedSceneId);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MoveSceneToEndAsync(string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            if (state.Scenes.All(scene => scene.Id != sceneId))
            {
                return;
            }

            RemoveSceneFromChapters(state.Chapters, sceneId);
            var chapter = state.Chapters.Chapters.LastOrDefault();
            if (chapter is null)
            {
                chapter = new ChapterIndexEntry { Name = "פרק 1" };
                state.Chapters.Chapters.Add(chapter);
            }

            chapter.SceneIds.Add(sceneId);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddChapterDividerBeforeSceneAsync(string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var chapterIndex = state.Chapters.Chapters.FindIndex(item => item.SceneIds.Contains(sceneId, StringComparer.Ordinal));
            if (chapterIndex < 0)
            {
                return;
            }

            var source = state.Chapters.Chapters[chapterIndex];
            var sceneIndex = source.SceneIds.IndexOf(sceneId);
            if (sceneIndex == 0)
            {
                return;
            }

            var newChapter = new ChapterIndexEntry
            {
                Name = CreateNextChapterName(state.Chapters),
                SceneIds = source.SceneIds.Skip(sceneIndex).ToList()
            };
            source.SceneIds.RemoveRange(sceneIndex, source.SceneIds.Count - sceneIndex);
            state.Chapters.Chapters.Insert(chapterIndex + 1, newChapter);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveChapterDividerAsync(string firstSceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var chapterIndex = state.Chapters.Chapters.FindIndex(item => item.SceneIds.FirstOrDefault() == firstSceneId);
            if (chapterIndex <= 0)
            {
                return;
            }

            var previous = state.Chapters.Chapters[chapterIndex - 1];
            previous.SceneIds.AddRange(state.Chapters.Chapters[chapterIndex].SceneIds);
            state.Chapters.Chapters.RemoveAt(chapterIndex);
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameChapterAsync(string firstSceneId, string newName)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await LoadOrderedStateCoreAsync();
            var chapter = state.Chapters.Chapters.FirstOrDefault(item => item.SceneIds.FirstOrDefault() == firstSceneId);
            if (chapter is null)
            {
                return;
            }

            chapter.Name = string.IsNullOrWhiteSpace(newName) ? "פרק ללא שם" : newName.Trim();
            await PersistStructureCoreAsync(state.Scenes, state.Chapters);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoryWorkspace> LoadWorkspaceCoreAsync()
    {
        EnsureFolders();
        await MigrateLegacyJsonScenesCoreAsync();
        await EnsureSeedSceneCoreAsync();
        await EnsureCharacterNamesFileCoreAsync();

        var state = await LoadOrderedStateCoreAsync();
        await WriteJsonAtomicAsync(ChaptersPath, state.Chapters);
        var characters = _metadata?.IsInitialized == true
            ? (await _metadata.LoadWorkspaceAsync()).Characters
            : await WriteCharactersIndexCoreAsync(state.Scenes, state.Chapters);
        return new StoryWorkspace
        {
            Scenes = state.Scenes,
            ChaptersIndex = state.Chapters,
            CharactersIndex = characters
        };
    }

    private async Task<(List<SceneDocument> Scenes, ChaptersIndexDocument Chapters)> LoadOrderedStateCoreAsync()
    {
        EnsureFolders();
        var sceneFiles = await LoadSceneFilesCoreAsync();
        var chapters = await ReadJsonAsync<ChaptersIndexDocument>(ChaptersPath) ?? BuildChaptersIndexFromLegacyMetadata(sceneFiles);
        NormalizeChapters(chapters, sceneFiles);

        var scenesById = sceneFiles.ToDictionary(scene => scene.Id, StringComparer.Ordinal);
        var orderedScenes = new List<SceneDocument>(sceneFiles.Count);
        foreach (var chapter in chapters.Chapters)
        {
            foreach (var sceneId in chapter.SceneIds)
            {
                if (!scenesById.Remove(sceneId, out var scene))
                {
                    continue;
                }

                scene.Chapter = chapter.Name;
                scene.Order = orderedScenes.Count + 1;
                orderedScenes.Add(scene);
            }
        }

        foreach (var scene in scenesById.Values.OrderBy(scene => scene.Order).ThenBy(scene => scene.CreatedAt))
        {
            var chapter = chapters.Chapters.Last();
            chapter.SceneIds.Add(scene.Id);
            scene.Chapter = chapter.Name;
            scene.Order = orderedScenes.Count + 1;
            orderedScenes.Add(scene);
        }

        return (orderedScenes, chapters);
    }

    private async Task PersistStructureCoreAsync(List<SceneDocument> scenes, ChaptersIndexDocument chapters)
    {
        NormalizeChapters(chapters, scenes);
        await WriteJsonAtomicAsync(ChaptersPath, chapters);
        var ordered = ApplyChapterOrder(scenes, chapters);
        if (_metadata?.IsInitialized != true) await WriteCharactersIndexCoreAsync(ordered, chapters);
        _activity?.MarkMutation();
    }

    private async Task<CharactersIndexDocument> WriteCharactersIndexCoreAsync(List<SceneDocument> scenes, ChaptersIndexDocument chapters)
    {
        var definitions = await LoadCharacterDefinitionsCoreAsync();
        var order = chapters.Chapters.SelectMany(chapter => chapter.SceneIds)
            .Select((sceneId, index) => (sceneId, index))
            .ToDictionary(item => item.sceneId, item => item.index, StringComparer.Ordinal);
        var characters = new List<CharacterIndexEntry>();

        foreach (var definition in definitions.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var terms = definition.Aliases.Prepend(definition.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sceneIds = scenes
                .Where(scene => terms.Any(term => ContainsWholeTerm(scene.Content, term)))
                .OrderBy(scene => order.GetValueOrDefault(scene.Id, int.MaxValue))
                .Select(scene => scene.Id)
                .ToList();
            if (sceneIds.Count > 0)
            {
                characters.Add(new CharacterIndexEntry
                {
                    Name = definition.Name,
                    Aliases = definition.Aliases,
                    SceneIds = sceneIds
                });
            }
        }

        var index = new CharactersIndexDocument { Characters = characters };
        await WriteJsonAtomicAsync(CharactersPath, index);
        return index;
    }

    private static bool ContainsWholeTerm(string content, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term.Trim())}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private async Task<List<CharacterDefinition>> LoadCharacterDefinitionsCoreAsync()
    {
        var lines = await File.ReadAllLinesAsync(CharacterNamesPath, Encoding.UTF8);
        return lines
            .Select(line => line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 0)
            .Select(parts => new CharacterDefinition(parts[0], parts.Skip(1).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<SceneDocument> ApplyChapterOrder(List<SceneDocument> scenes, ChaptersIndexDocument chapters)
    {
        var byId = scenes.ToDictionary(scene => scene.Id, StringComparer.Ordinal);
        var ordered = new List<SceneDocument>();
        foreach (var chapter in chapters.Chapters)
        {
            foreach (var id in chapter.SceneIds)
            {
                if (!byId.TryGetValue(id, out var scene))
                {
                    continue;
                }

                scene.Chapter = chapter.Name;
                scene.Order = ordered.Count + 1;
                ordered.Add(scene);
            }
        }

        return ordered;
    }

    private static void NormalizeChapters(ChaptersIndexDocument chapters, IReadOnlyCollection<SceneDocument> scenes)
    {
        var validIds = scenes.Select(scene => scene.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in chapters.Chapters)
        {
            chapter.Name = string.IsNullOrWhiteSpace(chapter.Name) ? "פרק ללא שם" : chapter.Name.Trim();
            chapter.SceneIds = chapter.SceneIds.Where(id => validIds.Contains(id) && seen.Add(id)).ToList();
        }

        chapters.Chapters.RemoveAll(chapter => chapter.SceneIds.Count == 0);
        if (chapters.Chapters.Count == 0)
        {
            chapters.Chapters.Add(new ChapterIndexEntry { Name = "פרק 1" });
        }
    }

    private static ChaptersIndexDocument BuildChaptersIndexFromLegacyMetadata(IEnumerable<SceneDocument> scenes)
    {
        var chapters = new ChaptersIndexDocument();
        foreach (var scene in scenes.OrderBy(scene => scene.Order).ThenBy(scene => scene.CreatedAt))
        {
            var name = string.IsNullOrWhiteSpace(scene.Chapter) ? "פרק 1" : scene.Chapter.Trim();
            var chapter = chapters.Chapters.LastOrDefault();
            if (chapter is null || chapter.Name != name)
            {
                chapter = new ChapterIndexEntry { Name = name };
                chapters.Chapters.Add(chapter);
            }

            chapter.SceneIds.Add(scene.Id);
        }

        return chapters;
    }

    private async Task<List<SceneDocument>> LoadSceneFilesCoreAsync()
    {
        var scenes = new List<SceneDocument>();
        foreach (var file in Directory.EnumerateFiles(ScenesRoot, "*.scene.md"))
        {
            scenes.Add(await ReadSceneMarkdownCoreAsync(file));
        }

        return scenes;
    }

    private async Task EnsureSeedSceneCoreAsync()
    {
        if (Directory.EnumerateFiles(ScenesRoot, "*.scene.md").Any())
        {
            return;
        }

        var seed = CreateBlankScene("פרק 1");
        seed.Title = "סצנת פתיחה";
        seed.Content = "דמות ראשית נכנסת אל הסצנה.\n\nכתבו כאן את הסצנה הראשונה.";
        await WriteSceneMarkdownCoreAsync(seed);
        await WriteJsonAtomicAsync(ChaptersPath, new ChaptersIndexDocument
        {
            Chapters = [new ChapterIndexEntry { Name = "פרק 1", SceneIds = [seed.Id] }]
        });
    }

    private async Task EnsureCharacterNamesFileCoreAsync()
    {
        if (File.Exists(CharacterNamesPath))
        {
            return;
        }

        var existing = await ReadJsonAsync<CharactersIndexDocument>(CharactersPath);
        var names = existing?.Characters.Select(character => character.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? [];
        if (names.Count == 0)
        {
            names.Add("דמות ראשית");
        }

        await WriteTextAtomicAsync(CharacterNamesPath, string.Join(Environment.NewLine, names.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private async Task MigrateLegacyJsonScenesCoreAsync()
    {
        foreach (var file in Directory.EnumerateFiles(ScenesRoot, "*.scene.json"))
        {
            await using var stream = File.OpenRead(file);
            var scene = await JsonSerializer.DeserializeAsync<SceneDocument>(stream, JsonOptions);
            if (scene is null)
            {
                continue;
            }

            scene.Id = CreateSceneId();
            await WriteSceneMarkdownCoreAsync(scene);
            File.Delete(file);
        }
    }

    private async Task<SceneDocument> ReadSceneMarkdownCoreAsync(string path)
    {
        var markdown = await File.ReadAllTextAsync(path, Encoding.UTF8);
        var (frontMatter, content) = SplitFrontMatter(markdown);
        var metadata = ParseFrontMatter(frontMatter);
        var fileId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        var places = new List<string>();
        if (metadata.TryGetValue("placesJson", out var placesJson))
        {
            try
            {
                places = JsonSerializer.Deserialize<List<string>>(placesJson, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                places = [];
            }
        }

        return new SceneDocument
        {
            Id = ReadString(metadata, "id", fileId),
            Order = ReadInt(metadata, "order", int.MaxValue),
            Title = ReadString(metadata, "title", "סצנה ללא שם"),
            Chapter = ReadString(metadata, "chapter", "פרק 1"),
            Summary = ReadString(metadata, "summary", ""),
            Timeline = ReadString(metadata, "timeline", ""),
            Places = places,
            Content = content.TrimStart('\r', '\n'),
            CreatedAt = ReadDate(metadata, "createdAt", File.GetCreationTimeUtc(path)),
            UpdatedAt = ReadDate(metadata, "updatedAt", File.GetLastWriteTimeUtc(path))
        };
    }

    private async Task WriteSceneMarkdownCoreAsync(SceneDocument scene, bool createBackup = false)
    {
        if (string.IsNullOrWhiteSpace(scene.Id) || scene.Id.StartsWith("scene-", StringComparison.OrdinalIgnoreCase))
        {
            scene.Id = CreateSceneId();
        }

        if (createBackup)
        {
            var previous = File.Exists(GetScenePath(scene.Id))
                ? await ReadSceneMarkdownCoreAsync(GetScenePath(scene.Id))
                : null;
            var clearingContent = !string.IsNullOrWhiteSpace(previous?.Content) && string.IsNullOrWhiteSpace(scene.Content);
            await BackupSceneIfDueCoreAsync(scene.Id, force: clearingContent);
            if (clearingContent)
                _logger?.LogWarning("Scene {SceneId} cleared; previous content preserved in local history. PreviousLength={PreviousLength}", scene.Id, previous!.Content.Length);
        }

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"id: {scene.Id}");
        builder.AppendLine($"title: {EscapeFrontMatter(scene.Title)}");
        builder.AppendLine($"summary: {EscapeFrontMatter(scene.Summary)}");
        builder.AppendLine($"timeline: {EscapeFrontMatter(scene.Timeline)}");
        builder.AppendLine($"placesJson: {EscapeFrontMatter(JsonSerializer.Serialize(scene.Places, JsonOptions))}");
        builder.AppendLine($"createdAt: {scene.CreatedAt:O}");
        builder.AppendLine($"updatedAt: {scene.UpdatedAt:O}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(scene.Content ?? "");
        await WriteTextAtomicAsync(GetScenePath(scene.Id), builder.ToString());
        _activity?.MarkMutation();
    }

    private async Task BackupSceneIfDueCoreAsync(string sceneId, bool force = false)
    {
        var source = GetScenePath(sceneId);
        if (!File.Exists(source))
        {
            return;
        }

        var directory = Path.Combine(HistoryRoot, sceneId);
        Directory.CreateDirectory(directory);
        var newest = Directory.EnumerateFiles(directory, "*.scene.md")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .FirstOrDefault();
        if (!force && newest is not null && DateTime.UtcNow - newest.CreationTimeUtc < BackupInterval)
        {
            return;
        }

        var backup = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.scene.md");
        await using (var sourceStream = File.OpenRead(source))
        await using (var backupStream = File.Create(backup))
        {
            await sourceStream.CopyToAsync(backupStream);
        }

        foreach (var oldFile in Directory.EnumerateFiles(directory, "*.scene.md")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(BackupsPerScene))
        {
            oldFile.Delete();
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static Task WriteJsonAtomicAsync<T>(string path, T value) =>
        WriteTextAtomicAsync(path, JsonSerializer.Serialize(value, JsonOptions));

    private static async Task WriteTextAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static (string FrontMatter, string Content) SplitFrontMatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return ("", markdown);
        }

        using var reader = new StringReader(markdown);
        _ = reader.ReadLine();
        var frontMatter = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line == "---")
            {
                return (frontMatter.ToString(), reader.ReadToEnd());
            }

            frontMatter.AppendLine(line);
        }

        return ("", markdown);
    }

    private static Dictionary<string, string> ParseFrontMatter(string frontMatter)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontMatter.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                metadata[line[..separator].Trim()] = UnescapeFrontMatter(line[(separator + 1)..].Trim());
            }
        }

        return metadata;
    }

    private void EnsureFolders()
    {
        Directory.CreateDirectory(StoryRoot);
        Directory.CreateDirectory(ScenesRoot);
        Directory.CreateDirectory(IndexesRoot);
        Directory.CreateDirectory(HistoryRoot);
    }

    private string GetScenePath(string sceneId) => Path.Combine(ScenesRoot, $"{sceneId}.scene.md");

    private static SceneDocument CreateBlankScene(string chapter) => new()
    {
        Id = CreateSceneId(),
        Title = "סצנה חדשה",
        Chapter = chapter,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static void RemoveSceneFromChapters(ChaptersIndexDocument chapters, string sceneId)
    {
        foreach (var chapter in chapters.Chapters)
        {
            chapter.SceneIds.Remove(sceneId);
        }

        chapters.Chapters.RemoveAll(chapter => chapter.SceneIds.Count == 0);
    }

    private static string EnsureUniqueChapterName(ChaptersIndexDocument chapters, string requestedName)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? "פרק מיובא" : requestedName.Trim();
        var names = chapters.Chapters.Select(chapter => chapter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} ({index})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string CreateNextChapterName(ChaptersIndexDocument chapters)
    {
        var names = chapters.Chapters.Select(chapter => chapter.Name).ToHashSet(StringComparer.Ordinal);
        for (var index = chapters.Chapters.Count + 1; ; index++)
        {
            var name = $"פרק {index}";
            if (!names.Contains(name))
            {
                return name;
            }
        }
    }

    private static string JoinSceneContent(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{second.TrimStart()}";
    }

    private static string CreateSceneId() => $"scn-{Guid.NewGuid():N}"[..16];
    private static void ApplyMetadataToScenes(StoryWorkspace workspace)
    {
        var locations = workspace.LocationsIndex.Entities.ToDictionary(entity => entity.Id, entity => entity.Name, StringComparer.Ordinal);
        foreach (var scene in workspace.Scenes)
        {
            if (!workspace.SceneMetadata.TryGetValue(scene.Id, out var metadata)) continue;
            scene.Summary = metadata.Summary;
            scene.Timeline = metadata.Time.Label;
            scene.Places = metadata.LocationIds.Where(locations.ContainsKey).Select(id => locations[id]).ToList();
        }
    }

    private static string EscapeFrontMatter(string? value) =>
        (value ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    private static string UnescapeFrontMatter(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    private static string ReadString(IReadOnlyDictionary<string, string> metadata, string key, string fallback) => metadata.TryGetValue(key, out var value) ? value : fallback;
    private static int ReadInt(IReadOnlyDictionary<string, string> metadata, string key, int fallback) =>
        metadata.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static DateTimeOffset ReadDate(IReadOnlyDictionary<string, string> metadata, string key, DateTimeOffset fallback) =>
        metadata.TryGetValue(key, out var value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : fallback;

    private sealed record CharacterDefinition(string Name, List<string> Aliases);
}
