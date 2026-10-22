using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>CRUD for agents, shared by the Blazor sidebar and the REST endpoints.</summary>
public sealed class AgentDirectory
{
    private readonly AppDbContext _db;

    public AgentDirectory(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<Agent>> ListAsync(CancellationToken cancellationToken = default)
        => _db.Agents.AsNoTracking()
            .OrderByDescending(agent => agent.IsManager)
            .ThenBy(agent => agent.Name)
            .ToListAsync(cancellationToken);

    public Task<Agent?> GetManagerAsync(CancellationToken cancellationToken = default)
        => _db.Agents.AsNoTracking()
            .OrderBy(agent => agent.Name)
            .FirstOrDefaultAsync(agent => agent.IsManager, cancellationToken);

    public async Task<Agent> CreateAsync(AgentFormModel form, CancellationToken cancellationToken = default)
    {
        Validate(form);

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = form.Name.Trim(),
            Role = form.Role.Trim(),
            SystemPrompt = form.SystemPrompt.Trim(),
            ModelId = NormalizeModel(form.ModelId),
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
        agent.ModelId = NormalizeModel(form.ModelId);

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
    }

    private static string NormalizeModel(string? modelId)
        => string.IsNullOrWhiteSpace(modelId) ? OmniRouteOptions.DefaultModel : modelId.Trim();
}
