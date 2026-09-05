using IsraeliAuthorStudio.Services;

namespace IsraeliAuthorStudio.Tests;

public sealed class StoryRepositoryTests
{
    [Fact]
    public async Task NewProjectStoresSequenceInChapterIndex()
    {
        await using var context = await TestContext.CreateAsync();

        var workspace = await context.Repository.LoadWorkspaceAsync();

        var scene = Assert.Single(workspace.Scenes);
        Assert.Equal(scene.Id, Assert.Single(workspace.ChaptersIndex.Chapters).SceneIds.Single());
        var markdown = await File.ReadAllTextAsync(Directory.EnumerateFiles(context.ScenesPath, "*.scene.md").Single());
        Assert.DoesNotContain("order:", markdown);
        Assert.DoesNotContain("chapter:", markdown);
    }

    [Fact]
    public async Task ReorderChangesIndexWithoutRewritingSceneFiles()
    {
        await using var context = await TestContext.CreateAsync();
        var first = (await context.Repository.LoadWorkspaceAsync()).Scenes.Single();
        var second = await context.Repository.CreateSceneAfterAsync(first.Id);
        var third = await context.Repository.CreateSceneAfterAsync(second!.Id);
        var before = Directory.EnumerateFiles(context.ScenesPath, "*.scene.md")
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.Ordinal);

        await context.Repository.ReorderSceneBeforeAsync(third!.Id, first.Id);

        var workspace = await context.Repository.LoadWorkspaceAsync();
        Assert.Equal([third.Id, first.Id, second.Id], workspace.Scenes.Select(scene => scene.Id));
        var after = Directory.EnumerateFiles(context.ScenesPath, "*.scene.md")
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.Ordinal);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SaveCreatesBackupAndLeavesNoTemporaryFiles()
    {
        await using var context = await TestContext.CreateAsync();
        var scene = (await context.Repository.LoadWorkspaceAsync()).Scenes.Single();

        await context.Repository.SaveSceneContentAsync(scene.Id, "גרסה חדשה");

        var workspace = await context.Repository.LoadWorkspaceAsync();
        Assert.Equal("גרסה חדשה", workspace.Scenes.Single().Content);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(context.ProjectPath, ".history", scene.Id), "*.scene.md"));
        Assert.Empty(Directory.EnumerateFiles(context.ProjectPath, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ClearingSceneWithinBackupIntervalPreservesLatestText()
    {
        await using var context = await TestContext.CreateAsync();
        var scene = (await context.Repository.LoadWorkspaceAsync()).Scenes.Single();
        await context.Repository.SaveSceneContentAsync(scene.Id, "First draft");
        await context.Repository.SaveSceneContentAsync(scene.Id, "Latest manuscript before clearing");

        await context.Repository.SaveSceneContentAsync(scene.Id, "");

        Assert.Empty((await context.Repository.LoadWorkspaceAsync()).Scenes.Single().Content);
        var backups = Directory.EnumerateFiles(Path.Combine(context.ProjectPath, ".history", scene.Id), "*.scene.md")
            .Select(File.ReadAllText).ToList();
        Assert.Contains(backups, backup => backup.Contains("Latest manuscript before clearing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitAndJoinRoundTripSceneContent()
    {
        await using var context = await TestContext.CreateAsync();
        var scene = (await context.Repository.LoadWorkspaceAsync()).Scenes.Single();
        await context.Repository.SaveSceneContentAsync(scene.Id, "חלק ראשון חלק שני");
        scene.Content = "חלק ראשון חלק שני";

        await context.Repository.SplitSceneAsync(scene, 9);
        var split = await context.Repository.LoadWorkspaceAsync();
        Assert.Equal(2, split.Scenes.Count);

        await context.Repository.JoinWithNextSceneAsync(split.Scenes[0]);
        var joined = await context.Repository.LoadWorkspaceAsync();
        Assert.Single(joined.Scenes);
        Assert.Contains("חלק ראשון", joined.Scenes[0].Content);
        Assert.Contains("חלק שני", joined.Scenes[0].Content);
    }

    [Fact]
    public async Task CharacterAliasesUseWholeTermMatching()
    {
        await using var context = await TestContext.CreateAsync();
        var scene = (await context.Repository.LoadWorkspaceAsync()).Scenes.Single();
        await File.WriteAllTextAsync(Path.Combine(context.ProjectPath, "Indexes", "character-names.txt"), "דן|דני");

        await context.Repository.SaveSceneContentAsync(scene.Id, "בדניאל אין התאמה");
        Assert.Empty((await context.Repository.LoadWorkspaceAsync()).CharactersIndex.Characters);

        await context.Repository.SaveSceneContentAsync(scene.Id, "דני הגיע");
        var character = Assert.Single((await context.Repository.LoadWorkspaceAsync()).CharactersIndex.Characters);
        Assert.Equal("דן", character.Name);
        Assert.Contains("דני", character.Aliases);
    }

    [Fact]
    public async Task ChapterCanBeCreatedAfterSingleSceneChapter()
    {
        await using var context = await TestContext.CreateAsync();
        var first = (await context.Repository.LoadWorkspaceAsync()).Scenes.Single();

        var created = await context.Repository.CreateChapterAfterAsync(first.Id);

        var workspace = await context.Repository.LoadWorkspaceAsync();
        Assert.NotNull(created);
        Assert.Equal(2, workspace.ChaptersIndex.Chapters.Count);
        Assert.Equal(created.Id, workspace.ChaptersIndex.Chapters[1].SceneIds.Single());
    }

    [Fact]
    public async Task FailedNewProjectDoesNotChangeCurrentProject()
    {
        await using var context = await TestContext.CreateAsync();
        var originalPath = context.Selection.CurrentProjectPath;
        var occupiedPath = Path.Combine(context.RootPath, "Occupied");
        Directory.CreateDirectory(Path.Combine(occupiedPath, "Scenes"));
        await File.WriteAllTextAsync(Path.Combine(occupiedPath, "Scenes", "existing.scene.md"), "existing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Repository.StartNewProjectAsync(occupiedPath));

        Assert.Equal(originalPath, context.Selection.CurrentProjectPath);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _root;
        public string RootPath => _root;
        public string ProjectPath { get; }
        public string ScenesPath => Path.Combine(ProjectPath, "Scenes");
        public ProjectSelectionService Selection { get; }
        public StoryRepository Repository { get; }

        private TestContext(string root, string projectPath, ProjectSelectionService selection, StoryRepository repository)
        {
            _root = root;
            ProjectPath = projectPath;
            Selection = selection;
            Repository = repository;
        }

        public static async Task<TestContext> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"IsraeliAuthorStudio-tests-{Guid.NewGuid():N}");
            var projectPath = Path.Combine(root, "Story");
            Directory.CreateDirectory(root);
            var environment = new TestWebHostEnvironment(root);
            var selection = new ProjectSelectionService(environment);
            var repository = new StoryRepository(selection);
            await repository.StartNewProjectAsync(projectPath);
            return new TestContext(root, projectPath, selection, repository);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
