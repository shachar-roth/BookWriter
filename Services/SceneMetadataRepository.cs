using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IsraeliAuthorStudio.Models;

namespace IsraeliAuthorStudio.Services;

public sealed class SceneMetadataRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly ProjectSelectionService _projects;

    public SceneMetadataRepository(ProjectSelectionService projects)
    {
        _projects = projects;
    }

    private string ProjectRoot => _projects.CurrentProjectPath;
    private string SceneMetadataRoot => Path.Combine(ProjectRoot, "Metadata", "Scenes");
    private string CharactersPath => Path.Combine(ProjectRoot, "Indexes", "characters.json");
    private string LocationsPath => Path.Combine(ProjectRoot, "Indexes", "locations.json");
    private string TimelinePath => Path.Combine(ProjectRoot, "Indexes", "timeline.json");
    private string MigrationMarkerPath => Path.Combine(ProjectRoot, ".studio", "metadata-v1");

    public bool IsInitialized => Directory.Exists(SceneMetadataRoot) && Directory.EnumerateFiles(SceneMetadataRoot, "*.json").Any();

    public async Task<bool> EnsureMigratedAsync(
        IReadOnlyList<SceneDocument> scenes,
        CharactersIndexDocument legacyCharacters,
        CancellationToken cancellationToken = default)
    {
        var hadSceneMetadata = IsInitialized;
        var initialMigration = !File.Exists(MigrationMarkerPath) && !hadSceneMetadata;
        Directory.CreateDirectory(SceneMetadataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(CharactersPath)!);
        var persistedCharacters = NormalizeCharacters(
            await ReadJsonAsync<CharactersIndexDocument>(CharactersPath, cancellationToken) ?? new CharactersIndexDocument());
        var characters = NormalizeCharacters(legacyCharacters).Characters.Count > 0
            ? legacyCharacters
            : persistedCharacters;
        foreach (var character in characters.Characters)
        {
            if (string.IsNullOrWhiteSpace(character.Id))
            {
                character.Id = CreateEntityId("char", character.Name);
            }
        }

        var locations = NormalizeEntities(
            await ReadJsonAsync<EntityIndexDocument>(LocationsPath, cancellationToken) ?? new EntityIndexDocument());
        foreach (var scene in scenes)
        {
            var path = GetSceneMetadataPath(scene.Id);
            if (File.Exists(path)) continue;

            var characterIds = characters.Characters
                .Where(character => character.SceneIds.Contains(scene.Id, StringComparer.Ordinal))
                .Select(character => character.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var locationIds = new List<string>();
            foreach (var place in scene.Places.Where(place => !string.IsNullOrWhiteSpace(place)))
            {
                var entity = FindOrCreateEntity(locations, "loc", place, []);
                if (!entity.SceneIds.Contains(scene.Id, StringComparer.Ordinal)) entity.SceneIds.Add(scene.Id);
                locationIds.Add(entity.Id);
            }

            var metadata = new SceneMetadataDocument
            {
                SceneId = scene.Id,
                Summary = scene.Summary,
                CharacterIds = characterIds,
                LocationIds = locationIds,
                Time = new SceneTimeMetadata
                {
                    Label = scene.Timeline,
                    Confidence = string.IsNullOrWhiteSpace(scene.Timeline) ? "unknown" : "manual"
                },
                Locks = new MetadataFieldLocks
                {
                    Summary = !string.IsNullOrWhiteSpace(scene.Summary),
                    Characters = characterIds.Count > 0,
                    Locations = locationIds.Count > 0,
                    Time = !string.IsNullOrWhiteSpace(scene.Timeline)
                },
                AnalyzedContentHash = ComputeContentHash(scene.Content)
            };
            await WriteJsonAtomicAsync(path, metadata, cancellationToken);
        }

        var validSceneIds = scenes.Select(scene => scene.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var orphan in Directory.EnumerateFiles(SceneMetadataRoot, "*.json")
                     .Where(path => !validSceneIds.Contains(Path.GetFileNameWithoutExtension(path))))
        {
            File.Delete(orphan);
        }

        RebuildMemberships(scenes, await LoadAllSceneMetadataAsync(cancellationToken), characters, locations);
        var timeline = await BuildTimelineAsync(scenes, cancellationToken);
        await WriteJsonAtomicAsync(CharactersPath, characters, cancellationToken);
        await WriteJsonAtomicAsync(LocationsPath, locations, cancellationToken);
        await WriteJsonAtomicAsync(TimelinePath, timeline, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(MigrationMarkerPath)!);
        if (!File.Exists(MigrationMarkerPath)) await File.WriteAllTextAsync(MigrationMarkerPath, "1\n", new UTF8Encoding(false), cancellationToken);
        return initialMigration;
    }

    public async Task<MetadataWorkspace> LoadWorkspaceAsync(CancellationToken cancellationToken = default) =>
        new(
            await LoadAllSceneMetadataAsync(cancellationToken),
            NormalizeCharacters(await ReadJsonAsync<CharactersIndexDocument>(CharactersPath, cancellationToken) ?? new CharactersIndexDocument()),
            NormalizeEntities(await ReadJsonAsync<EntityIndexDocument>(LocationsPath, cancellationToken) ?? new EntityIndexDocument()),
            NormalizeTimeline(await ReadJsonAsync<TimelineIndexDocument>(TimelinePath, cancellationToken) ?? new TimelineIndexDocument()));

    public async Task<IReadOnlyList<string>> GetStaleSceneIdsAsync(
        IReadOnlyList<SceneDocument> scenes,
        int maximum,
        CancellationToken cancellationToken = default)
    {
        var metadata = await LoadAllSceneMetadataAsync(cancellationToken);
        return scenes
            .Where(scene => !metadata.TryGetValue(scene.Id, out var item) || item.AnalyzedContentHash != ComputeContentHash(scene.Content) || item.AnalyzedAt is null)
            .Take(Math.Max(1, maximum))
            .Select(scene => scene.Id)
            .ToList();
    }

    public async Task<bool> ApplyAnalysisAsync(
        SceneDocument scene,
        string expectedContentHash,
        MetadataAnalysisResult analysis,
        string model,
        IReadOnlyList<SceneDocument> orderedScenes,
        bool replaceLockedFields = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(ComputeContentHash(scene.Content), expectedContentHash, StringComparison.Ordinal)) return false;
        var workspace = await LoadWorkspaceAsync(cancellationToken);
        var metadata = workspace.SceneMetadata.GetValueOrDefault(scene.Id) ?? new SceneMetadataDocument { SceneId = scene.Id };
        if (replaceLockedFields) metadata.Locks = new MetadataFieldLocks();
        if (!metadata.Locks.Summary) metadata.Summary = analysis.Summary.Trim();
        if (!metadata.Locks.Characters)
        {
            metadata.CharacterIds = analysis.Characters
                .Where(entity => !string.IsNullOrWhiteSpace(entity.Name))
                .Select(entity => FindOrCreateCharacter(workspace.Characters, entity).Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (!metadata.Locks.Locations)
        {
            metadata.LocationIds = analysis.Locations
                .Where(entity => !string.IsNullOrWhiteSpace(entity.Name))
                .Select(entity => FindOrCreateEntity(workspace.Locations, "loc", entity.Name, entity.Aliases).Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (!metadata.Locks.Time)
        {
            metadata.Time = new SceneTimeMetadata
            {
                Label = analysis.TimeLabel.Trim(),
                PlaceAfterSceneId = analysis.PlaceAfterSceneId,
                Confidence = NormalizeConfidence(analysis.TimeConfidence)
            };
        }
        metadata.AnalyzedContentHash = expectedContentHash;
        metadata.AnalyzedAt = DateTimeOffset.UtcNow;
        metadata.AnalyzerModel = model;
        workspace.SceneMetadata[scene.Id] = metadata;

        RebuildMemberships(orderedScenes, workspace.SceneMetadata, workspace.Characters, workspace.Locations);
        var timeline = BuildTimeline(orderedScenes, workspace.SceneMetadata, workspace.Timeline);
        await WriteJsonAtomicAsync(GetSceneMetadataPath(scene.Id), metadata, cancellationToken);
        await WriteJsonAtomicAsync(CharactersPath, workspace.Characters, cancellationToken);
        await WriteJsonAtomicAsync(LocationsPath, workspace.Locations, cancellationToken);
        await WriteJsonAtomicAsync(TimelinePath, timeline, cancellationToken);
        return true;
    }

    public async Task SaveManualAsync(
        string sceneId,
        string timeLabel,
        IEnumerable<string> locations,
        IReadOnlyList<SceneDocument> orderedScenes,
        CancellationToken cancellationToken = default)
    {
        var workspace = await LoadWorkspaceAsync(cancellationToken);
        var metadata = workspace.SceneMetadata.GetValueOrDefault(sceneId) ?? new SceneMetadataDocument { SceneId = sceneId };
        metadata.Time.Label = timeLabel.Trim();
        metadata.Time.Confidence = string.IsNullOrWhiteSpace(timeLabel) ? "unknown" : "manual";
        metadata.Locks.Time = true;
        metadata.LocationIds = locations
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .Select(location => FindOrCreateEntity(workspace.Locations, "loc", location.Trim(), []).Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        metadata.Locks.Locations = true;
        workspace.SceneMetadata[sceneId] = metadata;
        RebuildMemberships(orderedScenes, workspace.SceneMetadata, workspace.Characters, workspace.Locations);
        var timeline = BuildTimeline(orderedScenes, workspace.SceneMetadata, workspace.Timeline);
        await WriteJsonAtomicAsync(GetSceneMetadataPath(sceneId), metadata, cancellationToken);
        await WriteJsonAtomicAsync(LocationsPath, workspace.Locations, cancellationToken);
        await WriteJsonAtomicAsync(TimelinePath, timeline, cancellationToken);
    }

    public async Task SaveManualCharactersAsync(
        string sceneId,
        IEnumerable<string> characterNames,
        IReadOnlyList<SceneDocument> orderedScenes,
        CancellationToken cancellationToken = default)
    {
        var workspace = await LoadWorkspaceAsync(cancellationToken);
        var metadata = workspace.SceneMetadata.GetValueOrDefault(sceneId) ?? new SceneMetadataDocument { SceneId = sceneId };
        metadata.CharacterIds = characterNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => FindOrCreateCharacter(workspace.Characters, new InferredEntity { Name = name.Trim() }).Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        metadata.Locks.Characters = true;
        workspace.SceneMetadata[sceneId] = metadata;
        RebuildMemberships(orderedScenes, workspace.SceneMetadata, workspace.Characters, workspace.Locations);
        await WriteJsonAtomicAsync(GetSceneMetadataPath(sceneId), metadata, cancellationToken);
        await WriteJsonAtomicAsync(CharactersPath, workspace.Characters, cancellationToken);
    }

    public async Task UnlockAsync(string sceneId, CancellationToken cancellationToken = default)
    {
        var path = GetSceneMetadataPath(sceneId);
        var metadata = await ReadJsonAsync<SceneMetadataDocument>(path, cancellationToken);
        if (metadata is null) return;
        metadata.Locks = new MetadataFieldLocks();
        metadata.AnalyzedAt = null;
        await WriteJsonAtomicAsync(path, metadata, cancellationToken);
    }

    public static string ComputeContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? ""))).ToLowerInvariant();

    private async Task<Dictionary<string, SceneMetadataDocument>> LoadAllSceneMetadataAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SceneMetadataDocument>(StringComparer.Ordinal);
        if (!Directory.Exists(SceneMetadataRoot)) return result;
        foreach (var path in Directory.EnumerateFiles(SceneMetadataRoot, "*.json"))
        {
            var metadata = await ReadJsonAsync<SceneMetadataDocument>(path, cancellationToken);
            if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.SceneId))
            {
                result[metadata.SceneId] = NormalizeMetadata(metadata);
            }
        }
        return result;
    }

    private async Task<TimelineIndexDocument> BuildTimelineAsync(IReadOnlyList<SceneDocument> scenes, CancellationToken cancellationToken)
    {
        var metadata = await LoadAllSceneMetadataAsync(cancellationToken);
        var existing = await ReadJsonAsync<TimelineIndexDocument>(TimelinePath, cancellationToken) ?? new TimelineIndexDocument();
        return BuildTimeline(scenes, metadata, existing);
    }

    private static TimelineIndexDocument BuildTimeline(
        IReadOnlyList<SceneDocument> scenes,
        IReadOnlyDictionary<string, SceneMetadataDocument> metadata,
        TimelineIndexDocument existing)
    {
        var orderedIds = existing.Entries.Select(entry => entry.SceneId)
            .Where(id => scenes.Any(scene => scene.Id == id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var scene in scenes)
        {
            if (!orderedIds.Contains(scene.Id, StringComparer.Ordinal)) orderedIds.Add(scene.Id);
        }

        foreach (var sceneId in orderedIds.ToList())
        {
            if (!metadata.TryGetValue(sceneId, out var item) || string.IsNullOrWhiteSpace(item.Time.PlaceAfterSceneId)) continue;
            var afterIndex = orderedIds.IndexOf(item.Time.PlaceAfterSceneId);
            if (afterIndex < 0) continue;
            orderedIds.Remove(sceneId);
            afterIndex = orderedIds.IndexOf(item.Time.PlaceAfterSceneId);
            orderedIds.Insert(afterIndex + 1, sceneId);
        }

        return new TimelineIndexDocument
        {
            Entries = orderedIds.Select(id =>
            {
                var item = metadata.GetValueOrDefault(id);
                return new TimelineIndexEntry { SceneId = id, Label = item?.Time.Label ?? "", Confidence = item?.Time.Confidence ?? "unknown" };
            }).ToList()
        };
    }

    private static void RebuildMemberships(
        IReadOnlyList<SceneDocument> scenes,
        IReadOnlyDictionary<string, SceneMetadataDocument> metadata,
        CharactersIndexDocument characters,
        EntityIndexDocument locations)
    {
        var order = scenes.Select((scene, index) => (scene.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        foreach (var character in characters.Characters) character.SceneIds.Clear();
        foreach (var location in locations.Entities) location.SceneIds.Clear();
        foreach (var item in metadata.Values)
        {
            foreach (var id in item.CharacterIds)
            {
                var entity = characters.Characters.FirstOrDefault(character => character.Id == id);
                if (entity is not null && !entity.SceneIds.Contains(item.SceneId, StringComparer.Ordinal)) entity.SceneIds.Add(item.SceneId);
            }
            foreach (var id in item.LocationIds)
            {
                var entity = locations.Entities.FirstOrDefault(location => location.Id == id);
                if (entity is not null && !entity.SceneIds.Contains(item.SceneId, StringComparer.Ordinal)) entity.SceneIds.Add(item.SceneId);
            }
        }
        foreach (var character in characters.Characters) character.SceneIds = character.SceneIds.OrderBy(id => order.GetValueOrDefault(id, int.MaxValue)).ToList();
        foreach (var location in locations.Entities) location.SceneIds = location.SceneIds.OrderBy(id => order.GetValueOrDefault(id, int.MaxValue)).ToList();
    }

    private static CharacterIndexEntry FindOrCreateCharacter(CharactersIndexDocument document, InferredEntity inferred)
    {
        var entity = document.Characters.FirstOrDefault(item =>
            string.Equals(item.Name, inferred.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Any(alias => string.Equals(alias, inferred.Name.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (entity is null)
        {
            entity = new CharacterIndexEntry { Id = CreateEntityId("char", inferred.Name), Name = inferred.Name.Trim() };
            document.Characters.Add(entity);
        }
        entity.Aliases = entity.Aliases.Concat(inferred.Aliases)
            .Where(alias => !string.IsNullOrWhiteSpace(alias) && !string.Equals(alias.Trim(), entity.Name, StringComparison.OrdinalIgnoreCase))
            .Select(alias => alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return entity;
    }

    private static EntityIndexEntry FindOrCreateEntity(EntityIndexDocument document, string prefix, string name, IEnumerable<string> aliases)
    {
        var normalized = name.Trim();
        var entity = document.Entities.FirstOrDefault(item =>
            string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Any(alias => string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase)));
        if (entity is null)
        {
            entity = new EntityIndexEntry { Id = CreateEntityId(prefix, normalized), Name = normalized };
            document.Entities.Add(entity);
        }
        entity.Aliases = entity.Aliases.Concat(aliases)
            .Where(alias => !string.IsNullOrWhiteSpace(alias) && !string.Equals(alias.Trim(), entity.Name, StringComparison.OrdinalIgnoreCase))
            .Select(alias => alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return entity;
    }

    private static string CreateEntityId(string prefix, string name)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToUpperInvariant()))).ToLowerInvariant();
        return $"{prefix}-{hash[..12]}";
    }

    private static SceneMetadataDocument NormalizeMetadata(SceneMetadataDocument metadata)
    {
        metadata.SceneId ??= "";
        metadata.Summary ??= "";
        metadata.CharacterIds ??= [];
        metadata.LocationIds ??= [];
        metadata.Time ??= new SceneTimeMetadata();
        metadata.Time.Label ??= "";
        metadata.Time.Confidence = NormalizeConfidence(metadata.Time.Confidence);
        metadata.Locks ??= new MetadataFieldLocks();
        metadata.AnalyzedContentHash ??= "";
        metadata.AnalyzerModel ??= "";
        return metadata;
    }

    private static CharactersIndexDocument NormalizeCharacters(CharactersIndexDocument document)
    {
        document.Characters ??= [];
        foreach (var character in document.Characters)
        {
            character.Id ??= "";
            character.Name ??= "";
            character.Aliases ??= [];
            character.SceneIds ??= [];
        }
        return document;
    }

    private static EntityIndexDocument NormalizeEntities(EntityIndexDocument document)
    {
        document.Entities ??= [];
        foreach (var entity in document.Entities)
        {
            entity.Id ??= "";
            entity.Name ??= "";
            entity.Aliases ??= [];
            entity.SceneIds ??= [];
        }
        return document;
    }

    private static TimelineIndexDocument NormalizeTimeline(TimelineIndexDocument document)
    {
        document.Entries ??= [];
        foreach (var entry in document.Entries)
        {
            entry.SceneId ??= "";
            entry.Label ??= "";
            entry.Confidence = NormalizeConfidence(entry.Confidence);
        }
        return document;
    }

    private static string NormalizeConfidence(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "high" => "high",
        "medium" => "medium",
        "low" => "low",
        "manual" => "manual",
        _ => "unknown"
    };

    private string GetSceneMetadataPath(string sceneId) => Path.Combine(SceneMetadataRoot, $"{sceneId}.json");

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

public sealed record MetadataWorkspace(
    Dictionary<string, SceneMetadataDocument> SceneMetadata,
    CharactersIndexDocument Characters,
    EntityIndexDocument Locations,
    TimelineIndexDocument Timeline);
