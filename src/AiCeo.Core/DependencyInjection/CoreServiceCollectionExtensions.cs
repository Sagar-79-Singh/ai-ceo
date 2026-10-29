using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCeo.DependencyInjection;

/// <summary>
/// Registers everything the app needs, independent of the desktop shell. The PhotinoX desktop
/// host and the tests both call <see cref="AddAiCeoCore"/>, so there is one wiring to maintain.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the database, providers, tools and orchestrator.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databasePath">Absolute SQLite file path.</param>
    /// <param name="providerDefaults">Optional provider defaults; the built-in OmniRoute settings otherwise.</param>
    public static IServiceCollection AddAiCeoCore(
        this IServiceCollection services,
        string databasePath,
        OmniRouteOptions? providerDefaults = null)
    {
        services.AddSingleton<IOptions<OmniRouteOptions>>(
            Microsoft.Extensions.Options.Options.Create(providerDefaults ?? new OmniRouteOptions()));

        services.AddDbContextFactory<AppDbContext>(builder => builder.UseSqlite($"Data Source={databasePath}"));

        services.AddSingleton(new AppRuntimeInfo(
            databasePath,
            typeof(CoreServiceCollectionExtensions).Assembly
                .GetName().Version?.ToString(3) ?? "2.0.2"));

        // The gateway caches OpenAI clients per endpoint+key, so it is a singleton.
        services.AddSingleton<ILlmGateway, LlmGateway>();

        // v1.0.16: the user's LLM connection (provider/base URL/key/model) is a singleton so the
        // orchestrator, the directory and the health probe all see the values the setup screen saved.
        services.AddSingleton<LlmSetupProvider>();

        // v2.0.1: the device-flow sign-in (GitHub Copilot / ChatGPT Codex). The HttpClient is shared
        // and short-lived requests only, so a single instance is right for a desktop app.
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<DeviceFlowService>();
        services.AddSingleton<DeviceFlowCoordinator>();

        // v2.0.1: the CEO ↔ worker conversations. The hub is the one place that tells the UI a
        // thread changed; the store is scoped like the orchestrator that writes to it.
        services.AddSingleton<WorkerThreadHub>();
        services.AddScoped<WorkerThreadStore>();

        // v2.0.0: the human-in-the-loop gate. One instance for the whole process, because the
        // orchestrator (thread pool) parks the call and the Nexus presenter (message loop) answers it.
        services.AddSingleton<ApprovalCoordinator>();

        services.AddScoped<AgentDirectory>();
        services.AddScoped<AgentHealthService>();
        services.AddScoped<WorkspaceService>();

        // v2.0.0: named, persistent workspaces.
        services.AddScoped<SpaceService>();
        services.AddScoped<UserSettingsService>();
        services.AddScoped<ToolService>();
        services.AddScoped<AgentOrchestrator>();

        return services;
    }

    /// <summary>
    /// Creates the database, upgrades an older schema, seeds the default team and loads the user's
    /// LLM connection into <see cref="LlmSetupProvider"/>.
    /// </summary>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            await DbSeeder.SeedAsync(db, logger, cancellationToken);
        }

        var setup = services.GetRequiredService<LlmSetupProvider>();
        var connection = await setup.RefreshAsync(cancellationToken);

        logger.LogInformation(
            connection.IsConfigured
                ? "LLM connection ready: {Connection}"
                : "LLM connection: not configured yet — the setup screen will ask for it",
            connection.Describe());
    }
}
