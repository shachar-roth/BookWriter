using System.Text.Json.Serialization;

namespace IsraeliAuthorStudio.Models;

public sealed class StoryWorkspace
{
    public List<SceneDocument> Scenes { get; set; } = [];
    public ChaptersIndexDocument ChaptersIndex { get; set; } = new();
    public CharactersIndexDocument CharactersIndex { get; set; } = new();
    public EntityIndexDocument LocationsIndex { get; set; } = new();
    public TimelineIndexDocument TimelineIndex { get; set; } = new();
    public Dictionary<string, SceneMetadataDocument> SceneMetadata { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SceneDocument
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Chapter { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Content { get; set; } = "";
    public string Timeline { get; set; } = "";
    public List<string> Places { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChaptersIndexDocument
{
    public List<ChapterIndexEntry> Chapters { get; set; } = [];
}

public sealed class ChapterIndexEntry
{
    public string Name { get; set; } = "";
    public List<string> SceneIds { get; set; } = [];
}

public sealed class CharactersIndexDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<CharacterIndexEntry> Characters { get; set; } = [];
}

public sealed class CharacterIndexEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> SceneIds { get; set; } = [];
}

public sealed class DocxImportDocument
{
    public string SourceName { get; set; } = "";
    public List<DocxImportChapter> Chapters { get; set; } = [];
    public int SceneCount => Chapters.Sum(chapter => chapter.Scenes.Count);
}

public sealed class DocxImportChapter
{
    public string Name { get; set; } = "";
    public List<DocxImportScene> Scenes { get; set; } = [];
}

public sealed class DocxImportScene
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class SceneEditorModel
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Chapter { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Content { get; set; } = "";
    public string Timeline { get; set; } = "";
    public List<string> Places { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public string UpdatedAtLocalText => UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            var firstLine = Content
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine.Length <= 36 ? firstLine : $"{firstLine[..36]}...";
            }

            return string.IsNullOrWhiteSpace(Title) ? "סצנה ריקה" : Title;
        }
    }

    public static SceneEditorModel FromDocument(SceneDocument document) =>
        new()
        {
            Id = document.Id,
            Order = document.Order,
            Title = document.Title,
            Chapter = document.Chapter,
            Summary = document.Summary,
            Content = document.Content,
            Timeline = document.Timeline,
            Places = [.. document.Places],
            CreatedAt = document.CreatedAt,
            UpdatedAt = document.UpdatedAt
        };

    public SceneDocument ToDocument() =>
        new()
        {
            Id = Id,
            Order = Order,
            Title = Title.Trim(),
            Chapter = Chapter.Trim(),
            Summary = Summary,
            Content = Content,
            Timeline = Timeline.Trim(),
            Places = Places.Select(place => place.Trim()).Where(place => place.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
