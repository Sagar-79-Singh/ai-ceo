using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Data;

/// <summary>
/// Creates the SQLite database on first run (and upgrades v1.0.x schemas), then seeds one CEO
/// plus a small worker bench so the app is usable immediately. Safe to run on every start.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        // Upgrades a v1.0.x database in place before EF touches it.
        await SchemaUpgrader.UpgradeAsync(db, logger, cancellationToken);

        // Code-first: EnsureCreated builds the schema from the model. If the project is later
        // migrated with `dotnet ef migrations add`, the migrations path is used instead.
        var migrations = db.Database.GetMigrations().ToList();
        if (migrations.Count > 0)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        if (await db.Agents.AnyAsync(cancellationToken))
        {
            return;
        }

        db.Agents.AddRange(
            new Agent
            {
                Name = "Atlas",
                Role = "Chief Executive Officer",
                Provider = LlmProvider.OmniRoute,
                ModelId = LlmProviderCatalog.OmniRouteDefaultModel,
                IsManager = true,
                SystemPrompt =
                    "You are Atlas, the Chief Executive Officer of a small AI company. You are the single point of "
                    + "contact for the user. You break goals into crisp, sequential assignments for your workers, "
                    + "review what they return, and write the final answer the user reads. You never invent worker "
                    + "results and you never pretend work was done that was not."
            },
            new Agent
            {
                Name = "Scout",
                Role = "Researcher",
                Provider = LlmProvider.OmniRoute,
                ModelId = LlmProviderCatalog.OmniRouteDefaultModel,
                IsManager = false,
                SystemPrompt =
                    "You are Scout, a meticulous researcher. You gather and organise facts, call out assumptions, "
                    + "and cite the reasoning behind every claim. When you are unsure, you say so explicitly."
            },
            new Agent
            {
                Name = "Vector",
                Role = "Analyst",
                Provider = LlmProvider.OmniRoute,
                ModelId = LlmProviderCatalog.OmniRouteDefaultModel,
                IsManager = false,
                SystemPrompt =
                    "You are Vector, a quantitative analyst. You structure problems, weigh trade-offs, and produce "
                    + "decision-ready analysis with clear assumptions, numbers where possible, and a recommendation."
            },
            new Agent
            {
                Name = "Quill",
                Role = "Writer",
                Provider = LlmProvider.OmniRoute,
                ModelId = LlmProviderCatalog.OmniRouteDefaultModel,
                IsManager = false,
                SystemPrompt =
                    "You are Quill, a senior writer and editor. You turn raw findings into clear, well-structured prose "
                    + "for a busy executive audience. No filler, no hedging, no repetition."
            });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded default CEO and worker agents.");
    }

    public static async Task EnsureSingleManagerAsync(AppDbContext db, Guid managerId, CancellationToken cancellationToken = default)
    {
        var agents = await db.Agents.ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            agent.IsManager = agent.Id == managerId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
