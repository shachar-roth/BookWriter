using System.Runtime.CompilerServices;
using System.Net;
using System.Text;
using System.Text.Json;
using IsraeliAuthorStudio.Models;
using IsraeliAuthorStudio.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace IsraeliAuthorStudio.Tests;

public sealed class AssistantAndMetadataTests
{
    [Fact]
    public async Task ExistingMetadataRemainsVisibleWhenGitSetupFails()
    {
        await using var project = await TestProject.CreateAsync(1);
        var git = new GitRepositoryService(NullLogger<GitRepositoryService>.Instance);
        // An ancestor repository deterministically refuses setup for the story subfolder.
        var ancestor = await git.EnsureProjectRepositoryAsync(project.RootPath, "Test Author", "test@example.invalid");
        Assert.True(ancestor.Success, ancestor.Error);
        var workspace = await project.Repository.LoadWorkspaceAsync();
        var metadata = new SceneMetadataRepository(project.Selection);
        await metadata.EnsureMigratedAsync(workspace.Scenes, workspace.CharactersIndex);
        await metadata.SaveManualCharactersAsync(project.SceneIds[0], ["Test character"], workspace.Scenes);
        await metadata.SaveManualAsync(project.SceneIds[0], "Day one", ["Test location"], workspace.Scenes);
        var originalMetadata = await File.ReadAllTextAsync(Path.Combine(project.Selection.CurrentProjectPath,
            "Metadata", "Scenes", $"{project.SceneIds[0]}.json"));
        var rejected = await git.EnsureProjectRepositoryAsync(project.Selection.CurrentProjectPath);
        Assert.False(rejected.Success);

        var repository = new StoryRepository(project.Selection, new ProjectOperationCoordinator(),
            new ProjectActivityTracker(), git, metadata,
            new AssistantSettingsService(new TestWebHostEnvironment(project.RootPath), new MemoryCredentialStore()));
        var loaded = await repository.LoadWorkspaceAsync();

        Assert.Single(loaded.SceneMetadata);
        Assert.Contains(loaded.CharactersIndex.Characters, character => character.Name == "Test character");
        Assert.Contains(loaded.LocationsIndex.Entities, location => location.Name == "Test location");
        Assert.Equal("Day one", loaded.Scenes.Single().Timeline);
        Assert.Equal(["Test location"], loaded.Scenes.Single().Places);
        Assert.Equal(originalMetadata, await File.ReadAllTextAsync(Path.Combine(project.Selection.CurrentProjectPath,
            "Metadata", "Scenes", $"{project.SceneIds[0]}.json")));
    }

    [Fact]
    public void DesktopFlagEnablesDesktopLaunch()
    {
        Assert.True(DesktopApplication.IsDesktopLaunch(["--desktop"]));
        Assert.False(DesktopApplication.IsDesktopLaunch([]));
    }

    [Fact]
    public void ServerModeKeepsApplicationDataUnderContentRoot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"ias-content-{Guid.NewGuid():N}");

        var paths = ApplicationDataPaths.Create(desktopMode: false, contentRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "App_Data")), paths.RootPath);
    }

    [Fact]
    public void AutomaticAnalysisStartsAfterThirtySecondsIdle()
    {
        var options = new IdleSnapshotOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.IdleInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
    }

    [Fact]
    public void ProposalParserExtractsTypedOperationsAndHidesJson()
    {
        var response = "הכנתי שינוי.\n```agent-proposal\n{\"summary\":\"עדכון\",\"operations\":[{\"kind\":\"ReplaceSceneText\",\"sceneId\":\"scn-one\",\"content\":\"חדש\"}]}\n```";

        var parsed = AgentProposalParser.Extract(response);

        Assert.Equal("הכנתי שינוי.", parsed.DisplayText);
        var proposal = Assert.IsType<AgentProposal>(parsed.Proposal);
        Assert.Equal(AgentOperationKind.ReplaceSceneText, Assert.Single(proposal.Operations).Kind);
    }

    [Fact]
    public void StreamingProposalJsonIsNeverShown()
    {
        const string visible = "מצאתי את הסצנות הריקות.";
        var partialFence = AgentProposalParser.GetStreamingDisplayText($"{visible}\n```agent-pro");
        var completeProposal = AgentProposalParser.GetStreamingDisplayText(
            $"{visible}\n```agent-proposal\n{{\"summary\":\"מחיקה\",\"operations\":[");
        var rawProposal = AgentProposalParser.GetStreamingDisplayText(
            $"{visible}\n{{  \"summary\": \"מחיקה\",\"operations\":[");

        Assert.Equal(visible, partialFence);
        Assert.Equal(visible, completeProposal);
        Assert.Equal(visible, rawProposal);
    }

    [Fact]
    public void UnfencedProposalJsonBecomesProposalCardInsteadOfChatText()
    {
        const string response = """
            הכנתי את השינוי.
            { "summary":"מחיקה","operations":[{"kind":"DeleteScene","sceneId":"scn-one"}]}
            """;

        var parsed = AgentProposalParser.Extract(response);

        Assert.Equal("הכנתי את השינוי.", parsed.DisplayText);
        Assert.Equal(AgentOperationKind.DeleteScene, Assert.Single(Assert.IsType<AgentProposal>(parsed.Proposal).Operations).Kind);
    }

    [Fact]
    public void AgentDiffSeparatesInsertedDeletedAndUnchangedText()
    {
        var segments = AgentDiffService.Build("פתיחה ישנה לסצנה", "פתיחה חדשה לסצנה");

        Assert.Contains(segments, segment => segment.Kind == AgentDiffKind.Unchanged && segment.Text.Contains("פתיחה"));
        Assert.Contains(segments, segment => segment.Kind == AgentDiffKind.Deleted && segment.Text.Contains("ישנה"));
        Assert.Contains(segments, segment => segment.Kind == AgentDiffKind.Inserted && segment.Text.Contains("חדשה"));
    }

    [Fact]
    public async Task ConversationUsesIChatClientStreamingWithoutCloudCalls()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 3);
        var tools = new AssistantReadTools(context.Repository, context.Selection);
        var conversation = new AssistantConversationService(new FakeClientFactory("תשובה ", "בדוקה"), tools);

        var chunks = new List<string>();
        await foreach (var chunk in conversation.StreamAsync([], "מה קורה בסיפור?", context.SceneIds[0])) chunks.Add(chunk);

        Assert.Equal("תשובה בדוקה", string.Concat(chunks));
    }

    [Fact]
    public async Task ConversationExecutesStreamedGitToolCallsAndKeepsTheirJsonOutOfChat()
    {
        await using var context = await TestProject.CreateAsync(1);
        var git = new GitRepositoryService(NullLogger<GitRepositoryService>.Instance);
        var root = context.Selection.CurrentProjectPath;
        Assert.True((await git.EnsureProjectRepositoryAsync(root, "Writer", "writer@example.test")).Success);
        var hash = (await git.GetHeadAsync(root)).Output.Trim();
        var sceneId = context.SceneIds[0];
        var handler = new SequenceHttpHandler(
            ToolStream("call_history", "read_project_git", JsonSerializer.Serialize(new { operation = "History", sceneId })),
            ToolStream("call_scene", "read_project_git", JsonSerializer.Serialize(new { operation = "SceneAtCommit", sceneId, commitHash = hash })),
            TextStream("Found the previous scene."));
        var conversation = new AssistantConversationService(new HttpClientFactory(handler),
            new AssistantReadTools(context.Repository, context.Selection),
            new AssistantGitTools(context.Selection, git, new ProjectOperationCoordinator()));

        var output = new StringBuilder();
        await foreach (var chunk in conversation.StreamAsync([], "Show the saved history of this scene", sceneId)) output.Append(chunk);

        Assert.Equal("Found the previous scene.", output.ToString());
        Assert.Equal(3, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0]);
        var tool = Assert.Single(first.RootElement.GetProperty("tools").EnumerateArray()
            .Where(item => item.GetProperty("function").GetProperty("name").GetString() == "read_project_git")).GetProperty("function");
        Assert.Equal("read_project_git", tool.GetProperty("name").GetString());
        Assert.Contains("explicitly asks", tool.GetProperty("description").GetString());
        Assert.True(tool.GetProperty("parameters").GetProperty("properties").TryGetProperty("operation", out _));
        Assert.DoesNotContain(hash, handler.Requests[0]);
        using var second = JsonDocument.Parse(handler.Requests[1]);
        var historyResult = Assert.Single(second.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "tool"));
        Assert.Equal("call_history", historyResult.GetProperty("tool_call_id").GetString());
        Assert.Contains(hash, historyResult.GetProperty("content").GetString());
        using var third = JsonDocument.Parse(handler.Requests[2]);
        var sceneResult = third.RootElement.GetProperty("messages").EnumerateArray()
            .Last(message => message.GetProperty("role").GetString() == "tool");
        using var result = JsonDocument.Parse(sceneResult.GetProperty("content").GetString()!);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("מילתמפתח", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(hash, (await git.GetHeadAsync(root)).Output.Trim());
    }

    [Fact]
    public async Task OrdinaryConversationDoesNotAutomaticallyLoadGitHistory()
    {
        await using var context = await TestProject.CreateAsync(1);
        var handler = new SequenceHttpHandler(TextStream("An idea for your story."));
        var conversation = new AssistantConversationService(new HttpClientFactory(handler),
            new AssistantReadTools(context.Repository, context.Selection),
            new AssistantGitTools(context.Selection, new GitRepositoryService(NullLogger<GitRepositoryService>.Instance), new ProjectOperationCoordinator()));
        await foreach (var _ in conversation.StreamAsync([], "Suggest a plot twist", context.SceneIds[0])) { }
        Assert.Single(handler.Requests);
        using var request = JsonDocument.Parse(handler.Requests[0]);
        Assert.DoesNotContain(request.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("role").GetString() == "tool");
        Assert.False(Directory.Exists(Path.Combine(context.Selection.CurrentProjectPath, ".git")));
    }

    [Fact]
    public async Task FullBookToolWorkflowReadsHundredScenesWithBoundedRequestsAndWorkingNotes()
    {
        await using var project = await TestProject.CreateAsync(100, contentLength: 4500);
        var handler = new BookReviewHandler();
        var conversation = new AssistantConversationService(new HttpClientFactory(handler),
            new AssistantReadTools(project.Repository, project.Selection));
        var progress = new List<string>();
        var output = new StringBuilder();
        await foreach (var text in conversation.StreamAsync([], "Review the entire book, not a sample", project.SceneIds[0],
            progress: status => { progress.Add(status); return Task.CompletedTask; })) output.Append(text);

        Assert.Equal("Reviewed all 100 scenes.", output.ToString());
        Assert.Equal(100, handler.SceneIds.Count);
        Assert.InRange(handler.RequestCount, 7, 128);
        Assert.True(handler.MaximumContextCharacters < 100000);
        Assert.Contains(progress, status => status.Contains("100/100"));
        Assert.True(handler.FinalRequestHasNotes);
    }

    [Fact]
    public async Task PreparedProposalPreservesReadHashAndFlagsEditsMadeDuringResearch()
    {
        await using var project = await TestProject.CreateAsync(1);
        var original = (await project.Repository.LoadWorkspaceAsync()).Scenes.Single();
        var hash = SceneMetadataRepository.ComputeContentHash(original.Content);
        await project.Repository.SaveSceneContentAsync(original.Id, "The writer edited while the assistant was reading.");
        var service = new AgentProposalService(project.Repository, project.Selection,
            new GitRepositoryService(NullLogger<GitRepositoryService>.Instance), new ProjectOperationCoordinator());
        var proposal = await service.PrepareAsync(new AgentProposal { Operations = [new AgentOperation
            { Kind = AgentOperationKind.ReplaceSceneText, SceneId = original.Id, Content = "suggested", ExpectedContentHash = hash }] });
        Assert.True(proposal.IsStale);
        Assert.Equal(hash, proposal.Operations.Single().ExpectedContentHash);
        Assert.False((await service.ApplyAsync(proposal)).Success);
    }

    private sealed class BookReviewHandler : HttpMessageHandler
    {
        public HashSet<string> SceneIds { get; } = [];
        public int RequestCount { get; private set; }
        public int MaximumContextCharacters { get; private set; }
        public bool FinalRequestHasNotes { get; private set; }
        private int? _nextScene = 0;
        private int _nextOffset;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.True(RequestCount <= 128);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToList();
            MaximumContextCharacters = Math.Max(MaximumContextCharacters, messages.Sum(message => message.GetProperty("content").GetString()?.Length ?? 0));
            var results = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToList();
            string response;
            if (results.Count > 0 && results[^1].GetProperty("tool_call_id").GetString()!.StartsWith("read", StringComparison.Ordinal))
            {
                using var result = JsonDocument.Parse(results[^1].GetProperty("content").GetString()!);
                foreach (var chunk in result.RootElement.GetProperty("chunks").EnumerateArray())
                {
                    SceneIds.Add(chunk.GetProperty("sceneId").GetString()!);
                    Assert.NotEmpty(chunk.GetProperty("text").GetString()!);
                }
                var cursor = result.RootElement.GetProperty("nextSceneIndex");
                _nextScene = cursor.ValueKind == JsonValueKind.Null ? null : cursor.GetInt32();
                _nextOffset = result.RootElement.GetProperty("nextOffset").GetInt32();
                response = ToolStream($"notes-{RequestCount}", "keep_research_notes",
                    JsonSerializer.Serialize(new { notes = "Evidence retained from scenes: " + string.Join(",", SceneIds) }));
            }
            else if (_nextScene is null)
            {
                FinalRequestHasNotes = messages.Any(message => message.GetProperty("role").GetString() == "system" &&
                    message.GetProperty("content").GetString()!.Contains("Evidence retained from scenes:"));
                Assert.Contains(messages, message => message.GetProperty("content").GetString()!.Contains("100/100"));
                response = TextStream("Reviewed all 100 scenes.");
            }
            else response = ToolStream($"read-{RequestCount}", "read_manuscript", JsonSerializer.Serialize(new { sceneIndex = _nextScene, offset = _nextOffset }));
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8) };
        }
    }

    [Fact]
    public async Task GitToolIsBoundToProjectAndHasPerTurnBudget()
    {
        await using var context = await TestProject.CreateAsync(1);
        var tools = new AssistantGitTools(context.Selection,
            new GitRepositoryService(NullLogger<GitRepositoryService>.Instance), new ProjectOperationCoordinator());
        var function = tools.CreateForConversation();
        var arguments = new AIFunctionArguments { ["operation"] = "History" };
        for (var i = 0; i < 8; i++) await function.InvokeAsync(arguments);
        Assert.Contains("budget", JsonSerializer.Serialize(await function.InvokeAsync(arguments)));
        var newTurn = tools.CreateForConversation();
        var other = Directory.CreateDirectory(Path.Combine(context.RootPath, "OtherStory")).FullName;
        await context.Selection.SetCurrentProjectPathAsync(other);
        Assert.Contains("project changed", JsonSerializer.Serialize(await newTurn.InvokeAsync(arguments)));
    }

    [Fact]
    public async Task TruncatedToolStreamIsNotExecuted()
    {
        var handler = new SequenceHttpHandler(ToolStream("one", "read_project_git", "{\"operation\":\"History\"}")
            .Replace("\"finish_reason\":\"tool_calls\"", "\"finish_reason\":null"));
        using var client = new OpenAiCompatibleChatClient(new Uri("https://example.test/v1"), "fake", "test", messageHandler: handler);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "history")]))
                Assert.Empty(update.Contents.OfType<FunctionCallContent>());
        });
    }

    [Fact]
    public async Task NonStreamingToolCallsAndToolChoiceRoundTrip()
    {
        var body = """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"one","type":"function","function":{"name":"read_project_git","arguments":"{\"operation\":\"History\"}"}}]}}]}""";
        var handler = new SequenceHttpHandler(body);
        using var client = new OpenAiCompatibleChatClient(new Uri("https://example.test/v1"), "fake", "test", messageHandler: handler);
        var tool = AIFunctionFactory.Create(() => "test", "read_project_git");
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "history")],
            new ChatOptions { Tools = [tool], ToolMode = ChatToolMode.None });
        var call = Assert.Single(response.Messages.Single().Contents.OfType<FunctionCallContent>());
        Assert.Equal("one", call.CallId);
        Assert.Equal("History", call.Arguments!["operation"]!.ToString());
        using var request = JsonDocument.Parse(handler.Requests.Single());
        Assert.Equal("none", request.RootElement.GetProperty("tool_choice").GetString());
    }

    private static string ToolStream(string id, string name, string arguments)
    {
        // Provider tool arguments arrive over multiple SSE chunks, not as one JSON document.
        var half = arguments.Length / 2;
        var first = JsonSerializer.Serialize(new { choices = new[] { new { delta = new
        {
            tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments = arguments[..half] } } }
        } } } });
        var second = JsonSerializer.Serialize(new { choices = new[] { new { delta = new
        {
            tool_calls = new[] { new { index = 0, function = new { arguments = arguments[half..] } } }
        }, finish_reason = "tool_calls" } } });
        return $"data: {first}\n\ndata: {second}\n\ndata: {{\"choices\":[]}}\n\ndata: [DONE]\n\n";
    }

    private static string TextStream(string text) => "data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } }
    }) + "\n\ndata: [DONE]\n\n";

    private sealed class HttpClientFactory(HttpMessageHandler handler) : IAssistantClientFactory
    {
        public Task<IChatClient?> CreateAsync(bool useMetadataModel, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient?>(new OpenAiCompatibleChatClient(new Uri("https://example.test/v1"), "fake", "test", messageHandler: handler));
    }

    private sealed class SequenceHttpHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.InRange(Requests.Count, 1, responses.Length);
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8) };
        }
    }

    [Fact]
    public async Task MetadataMigrationPreservesManualValuesAndLocksThem()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 1);
        var workspace = await context.Repository.LoadWorkspaceAsync();
        workspace.Scenes[0].Summary = "סיכום ידני";
        workspace.Scenes[0].Timeline = "ערב";
        workspace.Scenes[0].Places = ["ירושלים"];
        var characters = new CharactersIndexDocument
        {
            Characters = [new CharacterIndexEntry { Name = "דנה", SceneIds = [workspace.Scenes[0].Id] }]
        };
        var metadata = new SceneMetadataRepository(context.Selection);

        Assert.True(await metadata.EnsureMigratedAsync(workspace.Scenes, characters));
        var migrated = await metadata.LoadWorkspaceAsync();
        var sceneMetadata = migrated.SceneMetadata[workspace.Scenes[0].Id];

        Assert.Equal("סיכום ידני", sceneMetadata.Summary);
        Assert.True(sceneMetadata.Locks.Summary);
        Assert.True(sceneMetadata.Locks.Characters);
        Assert.True(sceneMetadata.Locks.Locations);
        Assert.True(sceneMetadata.Locks.Time);
        Assert.Equal("ירושלים", Assert.Single(migrated.Locations.Entities).Name);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(migrated.Characters.Characters).Id));
    }

    [Fact]
    public async Task MetadataLoadNormalizesNullFieldsFromOlderProjects()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 1);
        var workspace = await context.Repository.LoadWorkspaceAsync();
        var metadata = new SceneMetadataRepository(context.Selection);
        await metadata.EnsureMigratedAsync(workspace.Scenes, new CharactersIndexDocument());
        var sceneId = workspace.Scenes[0].Id;
        var projectRoot = context.Selection.CurrentProjectPath;

        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "Metadata", "Scenes", $"{sceneId}.json"),
            $$"""{"sceneId":"{{sceneId}}","summary":null,"characterIds":null,"locationIds":null,"time":null,"locks":null,"analyzedContentHash":null,"analyzerModel":null}""");
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "Indexes", "characters.json"),
            """{"characters":[{"id":"char-one","name":"דנה","aliases":null,"sceneIds":null}]}""");
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "Indexes", "locations.json"),
            """{"entities":[{"id":"loc-one","name":"חיפה","aliases":null,"sceneIds":null}]}""");
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "Indexes", "timeline.json"),
            """{"entries":null}""");

        var loaded = await metadata.LoadWorkspaceAsync();
        var sceneMetadata = loaded.SceneMetadata[sceneId];

        Assert.NotNull(sceneMetadata.CharacterIds);
        Assert.NotNull(sceneMetadata.LocationIds);
        Assert.NotNull(sceneMetadata.Time);
        Assert.NotNull(sceneMetadata.Locks);
        Assert.NotNull(Assert.Single(loaded.Characters.Characters).Aliases);
        Assert.NotNull(Assert.Single(loaded.Locations.Entities).SceneIds);
        Assert.NotNull(loaded.Timeline.Entries);
    }

    [Fact]
    public async Task MetadataRejectsStaleAnalysisAndPreservesLockedFields()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 1);
        var workspace = await context.Repository.LoadWorkspaceAsync();
        var scene = workspace.Scenes[0];
        scene.Timeline = "זמן ידני";
        var metadata = new SceneMetadataRepository(context.Selection);
        await metadata.EnsureMigratedAsync(workspace.Scenes, new CharactersIndexDocument());
        await metadata.SaveManualAsync(scene.Id, scene.Timeline, [], workspace.Scenes);

        var stale = await metadata.ApplyAnalysisAsync(scene, "not-the-hash", new MetadataAnalysisResult { TimeLabel = "זמן שגוי" }, "cheap", workspace.Scenes);
        var valid = await metadata.ApplyAnalysisAsync(scene, SceneMetadataRepository.ComputeContentHash(scene.Content), new MetadataAnalysisResult
        {
            Summary = "סיכום אוטומטי",
            TimeLabel = "זמן שגוי",
            Characters = [new InferredEntity { Name = "יואב" }]
        }, "cheap", workspace.Scenes);
        var result = await metadata.LoadWorkspaceAsync();

        Assert.False(stale);
        Assert.True(valid);
        Assert.Equal("זמן ידני", result.SceneMetadata[scene.Id].Time.Label);
        Assert.Equal("סיכום אוטומטי", result.SceneMetadata[scene.Id].Summary);
    }

    [Fact]
    public async Task EmptyManualMetadataRemovesSceneMemberships()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 1);
        var workspace = await context.Repository.LoadWorkspaceAsync();
        var scene = workspace.Scenes[0];
        var metadata = new SceneMetadataRepository(context.Selection);
        await metadata.EnsureMigratedAsync(workspace.Scenes, new CharactersIndexDocument());
        await metadata.SaveManualCharactersAsync(scene.Id, ["דנה"], workspace.Scenes);
        await metadata.SaveManualAsync(scene.Id, "למחרת בבוקר", ["חיפה"], workspace.Scenes);

        await metadata.SaveManualCharactersAsync(scene.Id, [], workspace.Scenes);
        await metadata.SaveManualAsync(scene.Id, "", [], workspace.Scenes);
        var result = await metadata.LoadWorkspaceAsync();
        var sceneMetadata = result.SceneMetadata[scene.Id];

        Assert.Empty(sceneMetadata.CharacterIds);
        Assert.Empty(sceneMetadata.LocationIds);
        Assert.Empty(sceneMetadata.Time.Label);
        Assert.DoesNotContain(result.Characters.Characters, character => character.SceneIds.Contains(scene.Id));
        Assert.DoesNotContain(result.Locations.Entities, location => location.SceneIds.Contains(scene.Id));
        Assert.Equal("", Assert.Single(result.Timeline.Entries, entry => entry.SceneId == scene.Id).Label);
    }

    [Fact]
    public async Task HundredSceneInitialContextContainsNoSceneTextOrAutomaticSearchResults()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 100);
        var tools = new AssistantReadTools(context.Repository, context.Selection);

        var result = await tools.BuildContextAsync("מילתמפתח", context.SceneIds[75]);

        using var json = JsonDocument.Parse(result);
        Assert.Equal(100, json.RootElement.GetProperty("sceneCount").GetInt32());
        Assert.Equal(context.SceneIds[75], json.RootElement.GetProperty("activeSceneId").GetString());
        Assert.DoesNotContain("מילתמפתח", result);
        Assert.DoesNotContain(context.SceneIds[0], result);
        Assert.True(result.Length < 2000, $"Context was {result.Length} characters.");
    }

    [Fact]
    public async Task MetadataQueueLimitsHundredSceneBookToTenPerBatch()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 100);
        var workspace = await context.Repository.LoadWorkspaceAsync();
        var metadata = new SceneMetadataRepository(context.Selection);
        await metadata.EnsureMigratedAsync(workspace.Scenes, new CharactersIndexDocument());

        var stale = await metadata.GetStaleSceneIdsAsync(workspace.Scenes, maximum: 10);

        Assert.Equal(10, stale.Count);
        Assert.Equal(context.SceneIds.Take(10), stale);
    }

    [Fact]
    public async Task MetadataAnalysisUsesProviderDefaultTemperature()
    {
        const string response = """
            {
              "summary": "סיכום",
              "characters": [],
              "locations": [],
              "timeLabel": "",
              "placeAfterSceneId": null,
              "timeConfidence": "unknown"
            }
            """;
        var factory = new FakeClientFactory(response);
        var service = new MetadataAnalysisService(factory);

        var result = await service.AnalyzeAsync(
            new SceneDocument { Id = "scene", Content = "תוכן" },
            new MetadataWorkspace(
                new Dictionary<string, SceneMetadataDocument>(),
                new CharactersIndexDocument(),
                new EntityIndexDocument(),
                new TimelineIndexDocument()),
            "");

        Assert.NotNull(result);
        Assert.Null(factory.LastOptions?.Temperature);
    }

    [Fact]
    public async Task OpenAiClientSendsConfiguredMetadataReasoningEffort()
    {
        var handler = new RecordingHttpHandler();
        using var client = new OpenAiCompatibleChatClient(
            new Uri("https://example.test/v1"),
            "gpt-5.6-luna",
            "test-key",
            "none",
            handler);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "נתח")]);

        using var request = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("gpt-5.6-luna", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("none", request.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task StructuralMetadataChangesDoNotRepeatSchemaMigration()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 1);
        var workspace = await context.Repository.LoadWorkspaceAsync();
        var metadata = new SceneMetadataRepository(context.Selection);
        Assert.True(await metadata.EnsureMigratedAsync(workspace.Scenes, new CharactersIndexDocument()));
        var added = new SceneDocument { Id = "scn-added000001", Content = "new scene" };
        var withAdded = workspace.Scenes.Append(added).ToList();

        Assert.False(await metadata.EnsureMigratedAsync(withAdded, new CharactersIndexDocument()));
        Assert.Contains(added.Id, (await metadata.LoadWorkspaceAsync()).SceneMetadata.Keys);
        Assert.False(await metadata.EnsureMigratedAsync([added], new CharactersIndexDocument()));
        Assert.DoesNotContain(workspace.Scenes[0].Id, (await metadata.LoadWorkspaceAsync()).SceneMetadata.Keys);
    }

    [Fact]
    public async Task ImmediateMetadataAnalysisReplacesLockedFields()
    {
        await using var context = await TestProject.CreateAsync(sceneCount: 1);
        var environment = new TestWebHostEnvironment(context.RootPath);
        var credentials = new MemoryCredentialStore();
        var settings = new AssistantSettingsService(environment, credentials);
        await settings.SaveAsync(new AssistantSettings
        {
            Provider = new ProviderProfile
            {
                Endpoint = "https://example.test/v1",
                ChatModel = "chat-model",
                MetadataModel = "metadata-model"
            },
            GitAuthorName = "Test Author",
            GitAuthorEmail = "test@example.com"
        }, "test-key");

        var operations = new ProjectOperationCoordinator();
        var activity = new ProjectActivityTracker();
        var metadata = new SceneMetadataRepository(context.Selection);
        var git = new GitRepositoryService(NullLogger<GitRepositoryService>.Instance);
        var repository = new StoryRepository(
            context.Selection,
            operations,
            activity,
            git,
            metadata,
            settings);
        var workspace = await repository.LoadWorkspaceAsync();
        var scene = Assert.Single(workspace.Scenes);
        await repository.SaveSceneMetadataAsync(scene.Id, "זמן ידני", ["מקום ישן"]);
        await repository.SaveSceneCharactersAsync(scene.Id, ["דמות ישנה"]);

        const string response = """
            {
              "summary": "סיכום חדש",
              "characters": [{ "name": "יעל", "aliases": [] }],
              "locations": [{ "name": "חיפה", "aliases": [] }],
              "timeLabel": "למחרת",
              "placeAfterSceneId": null,
              "timeConfidence": "high"
            }
            """;
        var processor = new MetadataBatchProcessor(
            metadata,
            repository,
            new MetadataAnalysisService(new FakeClientFactory(response)),
            settings,
            activity,
            new IdleSnapshotOptions());

        var result = await processor.AnalyzeSceneNowAsync(scene.Id);
        var updated = await repository.LoadWorkspaceAsync();
        var sceneMetadata = updated.SceneMetadata[scene.Id];

        Assert.True(result.Success);
        Assert.Equal(MetadataAnalysisRunState.Applied, result.State);
        Assert.True(result.BackendDuration > TimeSpan.Zero);
        Assert.True(result.AiDuration >= TimeSpan.Zero);
        Assert.True(result.IndexUpdateDuration >= TimeSpan.Zero);
        Assert.True(result.BackendDuration >= result.AiDuration + result.IndexUpdateDuration);
        Assert.Equal("סיכום חדש", sceneMetadata.Summary);
        Assert.Equal("למחרת", sceneMetadata.Time.Label);
        Assert.Contains(updated.CharactersIndex.Characters, character =>
            character.Name == "יעל" && character.SceneIds.Contains(scene.Id));
        Assert.Contains(updated.LocationsIndex.Entities, location =>
            location.Name == "חיפה" && location.SceneIds.Contains(scene.Id));
        Assert.False(sceneMetadata.Locks.Summary);
        Assert.False(sceneMetadata.Locks.Characters);
        Assert.False(sceneMetadata.Locks.Locations);
        Assert.False(sceneMetadata.Locks.Time);
    }

    private sealed class FakeClientFactory(params string[] chunks) : IAssistantClientFactory
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<IChatClient?> CreateAsync(bool useMetadataModel, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient?>(new FakeChatClient(chunks, options => LastOptions = options));
    }

    private sealed class FakeChatClient(string[] chunks, Action<ChatOptions?>? observeOptions = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            observeOptions?.Invoke(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            observeOptions?.Invoke(options);
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(name));

        public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default)
        {
            _values[name] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"model\":\"gpt-5.6-luna\",\"choices\":[{\"message\":{\"content\":\"{}\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class TestProject : IAsyncDisposable
    {
        private readonly string _root;
        private TestProject(string root, ProjectSelectionService selection, StoryRepository repository, List<string> sceneIds)
        {
            _root = root;
            Selection = selection;
            Repository = repository;
            SceneIds = sceneIds;
        }

        public ProjectSelectionService Selection { get; }
        public StoryRepository Repository { get; }
        public List<string> SceneIds { get; }
        public string RootPath => _root;

        public static async Task<TestProject> CreateAsync(int sceneCount, int contentLength = 0)
        {
            var root = Path.Combine(Path.GetTempPath(), $"IsraeliAuthorStudio-assistant-tests-{Guid.NewGuid():N}");
            var project = Path.Combine(root, "Story");
            var scenesPath = Path.Combine(project, "Scenes");
            var indexesPath = Path.Combine(project, "Indexes");
            Directory.CreateDirectory(scenesPath);
            Directory.CreateDirectory(indexesPath);
            var ids = Enumerable.Range(1, sceneCount).Select(index => $"scn-{index:D12}").ToList();
            foreach (var (id, index) in ids.Select((id, index) => (id, index)))
            {
                var markdown = $"---\nid: {id}\ntitle: Scene {index + 1}\nsummary: \ntimeline: \nplacesJson: []\ncreatedAt: {DateTimeOffset.UtcNow:O}\nupdatedAt: {DateTimeOffset.UtcNow:O}\n---\n\nמילתמפתח תוכן סצנה {index + 1}";
                await File.WriteAllTextAsync(Path.Combine(scenesPath, $"{id}.scene.md"), markdown + new string('x', contentLength));
            }
            await File.WriteAllTextAsync(Path.Combine(indexesPath, "chapters.json"), System.Text.Json.JsonSerializer.Serialize(new ChaptersIndexDocument
            {
                Chapters = [new ChapterIndexEntry { Name = "פרק 1", SceneIds = ids }]
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            var environment = new TestWebHostEnvironment(root);
            var selection = new ProjectSelectionService(environment);
            await selection.SetCurrentProjectPathAsync(project);
            var repository = new StoryRepository(selection);
            return new TestProject(root, selection, repository, ids);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
                             .OrderByDescending(path => path.Length))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                Directory.Delete(_root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
