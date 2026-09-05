using IsraeliAuthorStudio.Components;
using IsraeliAuthorStudio.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var desktopMode = DesktopApplication.IsDesktopLaunch(args);
var contentRootPath = desktopMode ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();
var applicationData = ApplicationDataPaths.Create(desktopMode, contentRootPath);
using var desktopSession = desktopMode ? DesktopApplication.AcquireOrOpenExisting(applicationData.RootPath) : null;
if (desktopMode && desktopSession is null) return;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRootPath
});
if (desktopMode) builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.AddProvider(new LocalFileLoggerProvider(applicationData));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton(applicationData);
builder.Services.AddSingleton<ProjectSelectionService>();
builder.Services.AddSingleton<FolderPickerService>();
builder.Services.AddSingleton<DiagnosticBundleService>();
builder.Services.AddSingleton<ProjectOperationCoordinator>();
builder.Services.AddSingleton<ProjectActivityTracker>();
builder.Services.AddSingleton<IdleSnapshotOptions>();
builder.Services.AddSingleton<SyncStatusService>();
builder.Services.AddSingleton<GitRepositoryService>();
builder.Services.AddSingleton<ICredentialStore, PlatformCredentialStore>();
builder.Services.AddSingleton<AssistantSettingsService>();
builder.Services.AddSingleton<SceneMetadataRepository>();
builder.Services.AddSingleton<StoryRepository>();
builder.Services.AddSingleton<DocxImportService>();
builder.Services.AddSingleton<IAssistantClientFactory, AssistantClientFactory>();
builder.Services.AddSingleton<AssistantReadTools>();
builder.Services.AddSingleton<AssistantConversationService>();
builder.Services.AddSingleton<MetadataAnalysisService>();
builder.Services.AddSingleton<MetadataBatchProcessor>();
builder.Services.AddSingleton<ProjectMemoryService>();
builder.Services.AddSingleton<AgentProposalService>();
builder.Services.AddSingleton<IdleSnapshotCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<IdleSnapshotCoordinator>());

var app = builder.Build();
var applicationLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("IsraeliAuthorStudio.Application");
applicationLogger.LogInformation(
    "Application starting. DesktopMode={DesktopMode}, OS={OperatingSystem}, Architecture={Architecture}, Framework={Framework}.",
    desktopMode,
    System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

// Configure the HTTP request pipeline.
if (!desktopMode && !app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!desktopMode) app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/app-health", () => Results.Text("IsraeliAuthorStudio"));
app.MapGet("/diagnostics/export", async (
    HttpContext context,
    DiagnosticBundleService bundles,
    ILogger<DiagnosticBundleService> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        context.Response.Headers.CacheControl = "no-store";
        var bundle = await bundles.CreateAsync(cancellationToken);
        return Results.File(bundle.Content, "application/zip", bundle.FileName);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Diagnostic bundle creation failed.");
        return Results.Problem("Diagnostic bundle creation failed.");
    }
});
app.MapPost("/diagnostics/client-log", (
    ClientDiagnosticEvent clientEvent,
    ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("IsraeliAuthorStudio.Browser");
    var message = CleanClientDiagnosticValue(clientEvent.Message, 2_000);
    var stack = CleanClientDiagnosticValue(clientEvent.Stack, 8_000);
    var page = CleanClientDiagnosticValue(clientEvent.Page, 500);
    var browser = CleanClientDiagnosticValue(clientEvent.Browser, 1_000);
    if (string.Equals(clientEvent.Level, "info", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation(
            "Browser session: {Message}; Page={Page}; Viewport={Width}x{Height}; Browser={Browser}.",
            message,
            page,
            clientEvent.ViewportWidth,
            clientEvent.ViewportHeight,
            browser);
    }
    else
    {
        logger.LogWarning(
            "Browser error: {Message}; Page={Page}; Viewport={Width}x{Height}; Browser={Browser}; Stack={Stack}.",
            message,
            page,
            clientEvent.ViewportWidth,
            clientEvent.ViewportHeight,
            browser,
            stack);
    }
    return Results.NoContent();
}).DisableAntiforgery();

if (!desktopMode)
{
    app.Run();
    return;
}

await app.StartAsync();
var addresses = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()?.Addresses;
var address = addresses?.FirstOrDefault(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("The desktop server did not publish a local address.");
desktopSession!.PublishEndpoint(address);
DesktopApplication.OpenBrowser(address);
await app.WaitForShutdownAsync();

static string CleanClientDiagnosticValue(string? value, int maximumLength)
{
    var cleaned = (value ?? "").Replace('\0', ' ').Trim();
    return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
}
