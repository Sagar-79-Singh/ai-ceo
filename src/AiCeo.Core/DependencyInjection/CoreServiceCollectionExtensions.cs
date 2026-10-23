using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiCeo.DependencyInjection;

/// <summary>
/// Registers everything the app needs, independent of the desktop shell. The MAUI Blazor Hybrid
/// host and the tests both call <see cref="AddAiCeoCore"/>, so there is one wiring to maintain.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the database, providers, tools and orchestrator.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration (may be <c>null</c>; defaults are used).</param>
    /// <param name="databasePath">Absolute SQLite file path.</param>
    public static IServiceCollection AddAiCeoCore(
        this IServiceCollection services,
        IConfiguration? configuration,
        string databasePath)
    {
        services.AddHttpClient(LlmGateway.HttpClientName);

        if (configuration is not null)
        {
            services.Configure<OmniRouteOptions>(configuration.GetSection(OmniRouteOptions.SectionName));
        }
        else
        {
            services.Configure<OmniRouteOptions>(_ => { });
        }

        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        services.AddSingleton(new AppRuntimeInfo(
            databasePath,
            typeof(CoreServiceCollectionExtensions).Assembly
                .GetName().Version?.ToString(3) ?? "1.0.3"));

        // The gateway caches OpenAI clients per endpoint+key, so it is a singleton.
        services.AddSingleton<ILlmGateway, LlmGateway>();

        services.AddScoped<AgentDirectory>();
        services.AddScoped<AgentHealthService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<ToolService>();
        services.AddScoped<AgentOrchestrator>();

        return services;
    }

    /// <summary>Creates the database, upgrades an older schema and seeds the default team.</summary>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        Microsoft.Extensions.Logging.ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await DbSeeder.SeedAsync(db, logger, cancellationToken);
    }
}
