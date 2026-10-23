using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiCeo.Services;

/// <summary>CRUD for agents, shared by the Blazor sidebar and the REST endpoints.</summary>
public sealed class AgentDirectory
{
    private readonly AppDbContext _db;
    private readonly OmniRouteOptions _options;

    public AgentDirectory(AppDbContext db, IOptions<OmniRouteOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    /// <summary>Entities (with their keys) — for the orchestrator and health probes only.</summary>
    public Task<List<Agent>> ListAsync(CancellationToken cancellationToken = default)
        => _db.Agents.AsNoTracking()
            .OrderByDescending(agent => agent.IsManager)
            .ThenBy(agent => agent.Name)
            .ToListAsync(cancellationToken);

    /// <summary>Redacted read model — safe to hand to the UI or the REST API.</summary>
    public async Task<List<AgentView>> ListViewsAsync(CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken)).Select(ToView).ToList();

    public Task<Agent?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Agents.AsNoTracking().FirstOrDefaultAsync(agent => agent.Id == id, cancellationToken);

    public Task<Agent?> GetManagerAsync(CancellationToken cancellationToken = default)
        => _db.Agents.AsNoTracking()
            .OrderBy(agent => agent.Name)
            .FirstOrDefaultAsync(agent => agent.IsManager, cancellationToken);

    public AgentView ToView(Agent agent)
    {
        var baseUrl = LlmConnections.ResolveBaseUrl(agent.Provider, agent.BaseUrl, _options);
        var (apiKey, source) = LlmConnections.ResolveApiKey(agent.Provider, agent.ApiKey, _options);

        // Only mask keys the user actually stored; shared/fallback keys are reported as a source.
        var mask = !string.IsNullOrWhiteSpace(agent.ApiKey) ? LlmConnections.MaskKey(agent.ApiKey) : null;

        return AgentView.From(agent, baseUrl, mask, source);
    }

    public async Task<Agent> CreateAsync(AgentFormModel form, CancellationToken cancellationToken = default)
    {
        Validate(form);

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = form.Name.Trim(),
            Role = form.Role.Trim(),
            SystemPrompt = form.SystemPrompt.Trim(),
            Provider = form.Provider,
            BaseUrl = LlmConnections.NormalizeOptionalBaseUrl(form.Provider, form.BaseUrl),
            ApiKey = string.IsNullOrWhiteSpace(form.ApiKey) ? null : form.ApiKey.Trim(),
            ModelId = NormalizeModel(form.Provider, form.ModelId),
            // The first agent ever created becomes the CEO so the app is never headless.
            IsManager = !await _db.Agents.AnyAsync(cancellationToken)
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(cancellationToken);
        return agent;
    }

    public async Task<Agent?> UpdateAsync(Guid id, AgentFormModel form, CancellationToken cancellationToken = default)
    {
        Validate(form);

        var agent = await _db.Agents.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        agent.Name = form.Name.Trim();
        agent.Role = form.Role.Trim();
        agent.SystemPrompt = form.SystemPrompt.Trim();
        agent.Provider = form.Provider;
        agent.BaseUrl = LlmConnections.NormalizeOptionalBaseUrl(form.Provider, form.BaseUrl);
        agent.ModelId = NormalizeModel(form.Provider, form.ModelId);

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

        await _db.SaveChangesAsync(cancellationToken);
        return agent;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (agent is null)
        {
            return false;
        }

        _db.Agents.Remove(agent);
        await _db.SaveChangesAsync(cancellationToken);

        if (agent.IsManager)
        {
            var replacement = await _db.Agents
                .OrderBy(candidate => candidate.Name)
                .FirstOrDefaultAsync(cancellationToken);

            if (replacement is not null)
            {
                replacement.IsManager = true;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return true;
    }

    public async Task<Agent?> MakeManagerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agents = await _db.Agents.ToListAsync(cancellationToken);
        var target = agents.FirstOrDefault(agent => agent.Id == id);
        if (target is null)
        {
            return null;
        }

        foreach (var agent in agents)
        {
            agent.IsManager = agent.Id == id;
        }

        await _db.SaveChangesAsync(cancellationToken);
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

    private static string NormalizeModel(LlmProvider provider, string? modelId)
        => string.IsNullOrWhiteSpace(modelId)
            ? LlmProviderCatalog.DefaultModelFor(provider)
            : modelId.Trim();
}
