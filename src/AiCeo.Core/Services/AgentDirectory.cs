using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiCeo.Services;

/// <summary>
/// CRUD for agents, shared by the Blazor UI, the REST endpoints and the orchestrator. Every call
/// uses its own short-lived <see cref="AppDbContext"/> so a long-running agent run never shares a
/// context with the UI.
/// </summary>
public sealed class AgentDirectory
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly OmniRouteOptions _options;
    private readonly LlmSetupProvider _setup;

    public AgentDirectory(
        IDbContextFactory<AppDbContext> factory,
        IOptions<OmniRouteOptions> options,
        LlmSetupProvider setup)
    {
        _factory = factory;
        _options = options.Value;
        _setup = setup;
    }

    /// <summary>Entities (with their keys) — used by health probes only, never sent to a client.</summary>
    public async Task<List<Agent>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        return await db.Agents.AsNoTracking()
            .OrderByDescending(agent => agent.IsManager)
            .ThenBy(agent => agent.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Redacted read model — safe to hand to the UI or the REST API.</summary>
    public async Task<List<AgentView>> ListViewsAsync(CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken)).Select(ToView).ToList();

    public async Task<Agent?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Agents.AsNoTracking().FirstOrDefaultAsync(agent => agent.Id == id, cancellationToken);
    }

    public async Task<Agent?> GetManagerAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        return await db.Agents.AsNoTracking()
            .OrderBy(agent => agent.Name)
            .FirstOrDefaultAsync(agent => agent.IsManager, cancellationToken);
    }

    public AgentView ToView(Agent agent)
    {
        var setup = _setup.Current;
        var (provider, baseUrl, source) = LlmConnections.Effective(agent, setup, _options);

        // Only a key the user actually stored on the agent is shown as a mask. A key that comes from
        // the connection settings is reported through the source instead, so the secret itself never
        // travels to the page.
        var mask = !string.IsNullOrWhiteSpace(agent.ApiKey) ? LlmConnections.MaskKey(agent.ApiKey) : null;

        return AgentView.From(agent, baseUrl, mask, source) with
        {
            Provider = provider,
            ProviderDisplayName = LlmProviderCatalog.DisplayNameFor(provider),
            ModelId = LlmConnections.ResolveModel(agent, setup, provider)
        };
    }

    public async Task<Agent> CreateAsync(AgentFormModel form, CancellationToken cancellationToken = default)
    {
        Validate(form);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = form.Name.Trim(),
            Role = form.Role.Trim(),
            SystemPrompt = form.SystemPrompt.Trim(),
            Provider = form.Provider,
            BaseUrl = LlmConnections.NormalizeOptionalBaseUrl(form.Provider, form.BaseUrl),
            ApiKey = string.IsNullOrWhiteSpace(form.ApiKey) ? null : form.ApiKey.Trim(),
            ModelId = NormalizeModel(form.ModelId),
            CanUseTools = form.CanUseTools,
            // The first agent ever created becomes the CEO so the app is never headless.
            IsManager = !await db.Agents.AnyAsync(cancellationToken)
        };

        // The CEO coordinates only; it never gets direct file/terminal access.
        if (agent.IsManager)
        {
            agent.CanUseTools = false;
        }

        db.Agents.Add(agent);
        await db.SaveChangesAsync(cancellationToken);
        return agent;
    }

    public async Task<Agent?> UpdateAsync(Guid id, AgentFormModel form, CancellationToken cancellationToken = default)
    {
        Validate(form);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var agent = await db.Agents.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        agent.Name = form.Name.Trim();
        agent.Role = form.Role.Trim();
        agent.SystemPrompt = form.SystemPrompt.Trim();
        agent.Provider = form.Provider;
        agent.BaseUrl = LlmConnections.NormalizeOptionalBaseUrl(form.Provider, form.BaseUrl);
        agent.ModelId = NormalizeModel(form.ModelId);
        agent.CanUseTools = form.CanUseTools && !agent.IsManager;

        // Blank key means "unchanged"; an explicit clear removes it. The secret itself is never
        // round-tripped through the browser.
        if (!string.IsNullOrWhiteSpace(form.ApiKey))
        {
            agent.ApiKey = form.ApiKey.Trim();
        }
        else if (form.ClearApiKey)
        {
            agent.ApiKey = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return agent;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var agent = await db.Agents.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (agent is null)
        {
            return false;
        }

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken);

        if (agent.IsManager)
        {
            var replacement = await db.Agents
                .OrderBy(candidate => candidate.Name)
                .FirstOrDefaultAsync(cancellationToken);

            if (replacement is not null)
            {
                replacement.IsManager = true;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return true;
    }

    public async Task<Agent?> MakeManagerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var agents = await db.Agents.ToListAsync(cancellationToken);
        var target = agents.FirstOrDefault(agent => agent.Id == id);
        if (target is null)
        {
            return null;
        }

        foreach (var agent in agents)
        {
            agent.IsManager = agent.Id == id;

            if (agent.IsManager)
            {
                agent.CanUseTools = false;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return target;
    }

    private static void Validate(AgentFormModel form)
    {
        if (string.IsNullOrWhiteSpace(form.Name))
        {
            throw new ArgumentException("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(form.Role))
        {
            throw new ArgumentException("Role is required.");
        }

        if (string.IsNullOrWhiteSpace(form.SystemPrompt))
        {
            throw new ArgumentException("System prompt is required.");
        }

        if (!Enum.IsDefined(form.Provider))
        {
            throw new ArgumentException($"Unknown provider '{form.Provider}'.");
        }

        LlmConnections.ValidateForSave(form.Provider, form.BaseUrl);

        if (form.Provider == LlmProvider.Custom && string.IsNullOrWhiteSpace(form.ModelId))
        {
            throw new ArgumentException("The Custom provider requires a Model ID.");
        }
    }

    /// <summary>
    /// A blank model is stored as an empty string, which means "use the model from the user's
    /// connection settings". v1.0.16 deleted the hardcoded per-provider default it used to fall back
    /// to, so an agent can never silently run on a model nobody selected.
    /// </summary>
    private static string NormalizeModel(string? modelId)
        => string.IsNullOrWhiteSpace(modelId) ? string.Empty : modelId.Trim();
}
