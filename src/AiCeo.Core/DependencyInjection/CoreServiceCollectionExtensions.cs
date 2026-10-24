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
/// Registers everything the app needs, independent of the desktop shell. The Avalonia desktop
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
                .GetName().Version?.ToString(3) ?? "1.0.8"));

        // The gateway caches OpenAI clients per endpoint+key, so it is a singleton.
        services.AddSingleton<ILlmGateway, LlmGateway>();

        services.AddScoped<AgentDirectory>();
        services.AddScoped<AgentHealthService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<UserSettingsService>();
        services.AddScoped<ToolService>();
        services.AddScoped<AgentOrchestrator>();

        return services;
    }

    /// <summary>Creates the database, upgrades an older schema and seeds the default team.</summary>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await DbSeeder.SeedAsync(db, logger, cancellationToken);
    }
}
