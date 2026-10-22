using AiCeo.Components;
using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Content root is the app's base directory: normally the folder holding the executable, and for
// a single-file bundle (IncludeAllContentForSelfExtract) the extraction directory. Either way
// wwwroot and appsettings.json are found without relying on the current working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// A published single-file .exe should behave like a desktop app: if nobody configured URLs,
// listen on a friendly local port.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5088");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddHttpClient(OmniRouteChatService.HttpClientName);
builder.Services.Configure<OmniRouteOptions>(
    builder.Configuration.GetSection(OmniRouteOptions.SectionName));

var databasePath = DatabasePaths.Resolve(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton(new AppRuntimeInfo(
    databasePath,
    typeof(Agent).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"));

builder.Services.AddScoped<OmniRouteChatService>();
builder.Services.AddScoped<AgentDirectory>();
builder.Services.AddScoped<AgentOrchestrator>();

var app = builder.Build();

var omniRoute = app.Services.GetRequiredService<IOptions<OmniRouteOptions>>().Value;
app.Logger.LogInformation("AI CEO Orchestrator v{Version}", typeof(Agent).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
app.Logger.LogInformation("Content root: {ContentRoot}", app.Environment.ContentRootPath);
app.Logger.LogInformation("SQLite database: {DatabasePath}", databasePath);
app.Logger.LogInformation("OmniRoute endpoint: {Endpoint}", omniRoute.ResolveEndpoint());

// Create the SQLite database (Code-First) and seed the default CEO + workers.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DbSeeder.SeedAsync(db, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

// Classic static-file middleware on purpose: it serves wwwroot straight from the content root,
// which works both for a normal build and for the single-file .exe whose bundled content is
// extracted at startup. (MapStaticAssets requires a sidecar manifest file that the single-file
// bundler does not extract.)
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Double-clicking the published .exe should just work: open the UI once the server is up.
var launchUrl = BrowserLauncher.ResolveLaunchUrl(builder.Configuration);
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Open {Url} in your browser.", launchUrl);
    BrowserLauncher.TryOpen(app.Logger, launchUrl);
});

// ---------------------------------------------------------------------------------------------
// REST surface (docs/ARCHITECTURE.md): POST /api/task is the canonical entry point.
// ---------------------------------------------------------------------------------------------
var api = app.MapGroup("/api");

api.MapGet("/health", async (OmniRouteChatService llm, CancellationToken cancellationToken) =>
    Results.Ok(await llm.CheckHealthAsync(cancellationToken)));

api.MapGet("/agents", async (AgentDirectory directory, CancellationToken cancellationToken) =>
    Results.Ok(await directory.ListAsync(cancellationToken)));

api.MapPost("/agents", async (
    AgentFormModel form,
    AgentDirectory directory,
    CancellationToken cancellationToken) =>
{
    try
    {
        var agent = await directory.CreateAsync(form, cancellationToken);
        return Results.Created($"/api/agents/{agent.Id}", agent);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapPut("/agents/{id:guid}", async (
    Guid id,
    AgentFormModel form,
    AgentDirectory directory,
    CancellationToken cancellationToken) =>
{
    try
    {
        var agent = await directory.UpdateAsync(id, form, cancellationToken);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapDelete("/agents/{id:guid}", async (
    Guid id,
    AgentDirectory directory,
    CancellationToken cancellationToken) =>
    await directory.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

api.MapPost("/agents/{id:guid}/manager", async (
    Guid id,
    AgentDirectory directory,
    CancellationToken cancellationToken) =>
    await directory.MakeManagerAsync(id, cancellationToken) is null ? Results.NotFound() : Results.Ok());

api.MapGet("/tasks", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    var tasks = await db.Tasks.AsNoTracking()
        .OrderByDescending(task => task.CreatedAt)
        .Take(25)
        .Select(task => new
        {
            task.Id,
            task.UserPrompt,
            task.CeoPlan,
            task.FinalOutput,
            task.CreatedAt
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(tasks);
});

api.MapGet("/tasks/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
{
    var task = await db.Tasks.AsNoTracking()
        .Include(candidate => candidate.Logs)
        .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

    return task is null ? Results.NotFound() : Results.Ok(task);
});

api.MapPost("/task", async (
    TaskRequest request,
    AgentOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request?.Prompt))
    {
        return Results.BadRequest(new { error = "prompt is required" });
    }

    try
    {
        var task = await orchestrator.RunAsync(request.Prompt, cancellationToken: cancellationToken);

        return Results.Ok(new
        {
            task.Id,
            task.UserPrompt,
            task.CeoPlan,
            task.FinalOutput,
            task.CreatedAt
        });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
