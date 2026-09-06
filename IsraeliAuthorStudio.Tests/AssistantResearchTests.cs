using System.Text.Json;
using IsraeliAuthorStudio.Models;
using IsraeliAuthorStudio.Services;
using Microsoft.Extensions.AI;

namespace IsraeliAuthorStudio.Tests;

public sealed class AssistantResearchTests
{
    [Fact]
    public async Task AnySceneCanBeReadAndSearchFindsLateBookMatches()
    {
        var workspace = Book(100);
        workspace.Scenes[99].Content = "דן ידע על הסוד. דן שתק.";
        var session = Session(workspace);
        var search = Json(await session.SearchAsync("דן", count: 1));
        Assert.Equal(2, search.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, search.GetProperty("nextSkip").GetInt32());
        Assert.Equal("scn-99", search.GetProperty("items")[0].GetProperty("sceneId").GetString());
        var second = Json(await session.SearchAsync("דן", skip: 1, count: 1));
        Assert.Null(second.GetProperty("nextSkip").GetString());
        Assert.True(second.GetProperty("items")[0].GetProperty("offset").GetInt32() > 0);
        Assert.Equal(0, session.FullyReadScenes);
        var scene = Assert.IsType<SceneTextPage>(await session.ReadSceneAsync("scn-99"));
        Assert.Equal(workspace.Scenes[99].Content, scene.Text);
        Assert.Equal(1, session.FullyReadScenes);
        Assert.DoesNotContain(workspace.Scenes[99].Content, session.InitialContext);
    }

    [Fact]
    public async Task LongScenePaginationPreservesUnicodeAndTracksActualCoverage()
    {
        var book = Book(1);
        book.Scenes[0].Content = new string('a', 11999) + "\U0001F600" + new string('b', 14000);
        var session = Session(book);
        var last = Assert.IsType<SceneTextPage>(await session.ReadSceneAsync("scn-0", 24000));
        Assert.Equal(0, session.FullyReadScenes);
        var first = Assert.IsType<SceneTextPage>(await session.ReadSceneAsync("scn-0"));
        Assert.Equal(11999, first.NextOffset);
        var middle = Assert.IsType<SceneTextPage>(await session.ReadSceneAsync("scn-0", first.NextOffset!.Value));
        var end = Assert.IsType<SceneTextPage>(await session.ReadSceneAsync("scn-0", middle.NextOffset!.Value));
        Assert.Equal(book.Scenes[0].Content, first.Text + middle.Text + end.Text);
        Assert.Equal(1, session.FullyReadScenes);
        Assert.Equal(SceneMetadataRepository.ComputeContentHash(book.Scenes[0].Content), first.ContentHash);
        Assert.NotEmpty(last.Text);
    }

    [Fact]
    public async Task SequentialReadingCoversEverySceneAcrossChaptersIncludingEmptyScenes()
    {
        var book = Book(100);
        book.Scenes[17].Content = "";
        book.Scenes[70].Content = new string('z', 26000);
        var session = Session(book);
        var read = new Dictionary<string, string>();
        int? index = 0;
        var offset = 0;
        while (index is not null)
        {
            var page = Json(await session.ReadManuscriptAsync(index.Value, offset));
            foreach (var scene in page.GetProperty("chunks").EnumerateArray())
            {
                var id = scene.GetProperty("sceneId").GetString()!;
                read[id] = read.GetValueOrDefault(id, "") + scene.GetProperty("text").GetString();
            }
            index = page.GetProperty("nextSceneIndex").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextSceneIndex").GetInt32();
            offset = page.GetProperty("nextOffset").GetInt32();
        }
        Assert.Equal(100, session.FullyReadScenes);
        Assert.Equal(100, read.Count);
        foreach (var scene in book.Scenes) Assert.Equal(scene.Content, read[scene.Id]);
    }

    [Fact]
    public async Task IndexesExposeAliasesMembershipAndIndependentTimelineOrder()
    {
        var book = Book(3);
        book.CharactersIndex.Characters.Add(new() { Id = "char-dan", Name = "דניאל", Aliases = ["דני"], SceneIds = ["scn-2"] });
        book.LocationsIndex.Entities.Add(new() { Id = "loc-port", Name = "הנמל", Aliases = ["רציף"], SceneIds = ["scn-1"] });
        book.TimelineIndex.Entries = [new() { SceneId = "scn-2", Label = "אתמול" }, new() { SceneId = "scn-0", Label = "היום" }];
        book.SceneMetadata["scn-2"] = new() { SceneId = "scn-2", Summary = "סוד", CharacterIds = ["char-dan"], Locks = new() { Characters = true } };
        var session = Session(book);
        var chars = Json(await session.ReadIndexAsync("characters", "דני"));
        Assert.Contains("char-dan", chars.GetProperty("text").GetString());
        Assert.Contains("loc-port", Json(await session.ReadIndexAsync("locations", "רציף")).GetProperty("text").GetString());
        using var timeline = JsonDocument.Parse(Json(await session.ReadIndexAsync("timeline")).GetProperty("text").GetString()!);
        Assert.Equal("scn-2", timeline.RootElement[0].GetProperty("sceneId").GetString());
        using var metadata = JsonDocument.Parse(Json(await session.ReadMetadataAsync("scn-2")).GetProperty("text").GetString()!);
        Assert.True(metadata.RootElement.GetProperty("analysisIsStale").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("metadata").GetProperty("locks").GetProperty("characters").GetBoolean());
    }

    [Fact]
    public async Task NotesAndCoverageAreTurnLocalAndProjectSwitchStopsRetrieval()
    {
        var book = Book(2);
        var current = true;
        var progress = new List<string>();
        var session = new AssistantResearchSession(book, "scn-0", "selected passage", "memory", () => current,
            status => { progress.Add(status); return Task.CompletedTask; });
        await session.ReadSceneAsync("scn-0");
        await session.KeepNotesAsync("Evidence from scn-0");
        Assert.Contains("Evidence from scn-0", session.CoverageContext);
        Assert.Contains("selected passage", session.InitialContext);
        Assert.NotEmpty(progress);
        Assert.Empty(Session(book).Notes);
        Assert.Equal(0, Session(book).FullyReadScenes);
        current = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ListScenesAsync());
    }

    [Fact]
    public async Task MemoryAndSelectionStartSmallButFullMemoryRemainsReadable()
    {
        var memory = new string('a', 15000) + "late decision";
        var session = new AssistantResearchSession(Book(1), "scn-0", new string('s', 4000), memory, () => true);
        using var initial = JsonDocument.Parse(session.InitialContext);
        Assert.Equal(2000, initial.RootElement.GetProperty("selectedText").GetString()!.Length);
        Assert.True(initial.RootElement.GetProperty("selectionTruncated").GetBoolean());
        Assert.True(session.InitialContext.Length < 7000);
        var tail = Json(await session.ReadMemoryAsync(15000));
        Assert.Equal("late decision", tail.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ManuscriptPageDoesNotSplitSurrogateBetweenScenes()
    {
        var book = Book(2);
        book.Scenes[0].Content = new string('a', 11999);
        book.Scenes[1].Content = "\U0001F600";
        var session = Session(book);
        var first = Json(await session.ReadManuscriptAsync());
        Assert.Equal(1, first.GetProperty("nextSceneIndex").GetInt32());
        var second = Json(await session.ReadManuscriptAsync(1));
        Assert.Equal("\U0001F600", second.GetProperty("chunks")[0].GetProperty("text").GetString());
        Assert.Equal(2, session.FullyReadScenes);
    }

    [Fact]
    public async Task CancellationDuringProgressDoesNotRetrieveAnotherScene()
    {
        using var cancel = new CancellationTokenSource();
        var session = new AssistantResearchSession(Book(1), null, null, "", () => true,
            _ => { cancel.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadSceneAsync("scn-0", cancellationToken: cancel.Token));
        Assert.Equal(0, session.FullyReadScenes);
    }

    [Fact]
    public async Task InvalidPathsPagingAndCancellationNeverReadOtherFiles()
    {
        var session = Session(Book(1));
        Assert.True(Json(await session.ReadSceneAsync("../../assistant-settings.json")).TryGetProperty("error", out _));
        Assert.True(Json(await session.ReadIndexAsync("../.git/config")).TryGetProperty("error", out _));
        Assert.True(Json(await session.ReadSceneAsync("scn-0", -1)).TryGetProperty("error", out _));
        Assert.True(Json(await session.KeepNotesAsync(new string('x', 12001))).TryGetProperty("error", out _));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SearchAsync("text", cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task BoundedModelContextRetainsNotesAndCompleteToolCallPairs()
    {
        var session = Session(Book(10));
        await session.KeepNotesAsync("Important early finding: scn-0");
        using var client = new ResearchContextChatClient(new UnusedClient(), session, 2);
        var messages = new List<ChatMessage> { new(ChatRole.System, session.InitialContext), new(ChatRole.User, "Review book") };
        for (var i = 0; i < 100; i++)
        {
            messages.Add(new(ChatRole.Assistant, [new FunctionCallContent($"call-{i}", "read_scene", new Dictionary<string, object?> { ["sceneId"] = $"scn-{i}" })]));
            messages.Add(new(ChatRole.Tool, [new FunctionResultContent($"call-{i}", new string('x', 12000))]));
        }
        var bounded = client.BoundContext(messages);
        Assert.True(bounded.Count < 12);
        Assert.Contains(bounded, message => message.Text.Contains("Important early finding"));
        var callIds = bounded.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).ToHashSet();
        foreach (var result in bounded.SelectMany(message => message.Contents).OfType<FunctionResultContent>()) Assert.Contains(result.CallId, callIds);
        Assert.Contains("call-99", callIds);
        Assert.DoesNotContain("call-0", callIds);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static AssistantResearchSession Session(StoryWorkspace book) => new(book, "scn-0", null, "", () => true);
    private static StoryWorkspace Book(int count)
    {
        var scenes = Enumerable.Range(0, count).Select(index => new SceneDocument { Id = $"scn-{index}", Order = index, Content = $"Scene text {index}", Chapter = $"Chapter {index / 10}" }).ToList();
        return new() { Scenes = scenes, ChaptersIndex = new() { Chapters = scenes.Chunk(10).Select((chunk, index) => new ChapterIndexEntry
            { Name = $"Chapter {index}", SceneIds = chunk.Select(scene => scene.Id).ToList() }).ToList() } };
    }

    private sealed class UnusedClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
