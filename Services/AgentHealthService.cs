using AiCeo.Models;
using AiCeo.Options;
using Microsoft.Extensions.Options;

namespace AiCeo.Services;

/// <summary>
/// Probes the provider behind an agent. Runs server-side so the browser never needs (or sees) an
/// API key.
/// </summary>
public sealed class AgentHealthService
{
    private readonly AgentDirectory _directory;
    private readonly ILlmGateway _gateway;
    private readonly OmniRouteOptions _options;

    public AgentHealthService(AgentDirectory directory, ILlmGateway gateway, IOptions<OmniRouteOptions> options)
    {
        _directory = directory;
        _gateway = gateway;
        _options = options.Value;
    }

    public async Task<ProviderHealth> CheckManagerAsync(CancellationToken cancellationToken = default)
    {
        var manager = await _directory.GetManagerAsync(cancellationToken);
        if (manager is null)
        {
            return new ProviderHealth(false, "No CEO agent configured", "—", "—", "—");
        }

        return await CheckAsync(manager, cancellationToken);
    }

    public async Task<ProviderHealth> CheckAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await _directory.FindAsync(agentId, cancellationToken);
        if (agent is null)
        {
            return new ProviderHealth(false, "Agent not found", "—", "—", "—");
        }

        return await CheckAsync(agent, cancellationToken);
    }

    public async Task<ProviderHealth> CheckAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        if (!LlmConnections.TryResolve(agent, _options, out var connection, out var error) || connection is null)
        {
            return new ProviderHealth(
                false,
                error ?? "Provider is not configured",
                LlmProviderCatalog.DisplayNameFor(agent.Provider),
                agent.BaseUrl ?? LlmProviderCatalog.DefaultBaseUrlFor(agent.Provider),
                agent.ModelId);
        }

        var probe = LlmCall.Probe(
            agent.Name,
            connection.Provider,
            connection.BaseUrl,
            connection.ApiKey,
            connection.ModelId);

        return await _gateway.CheckAsync(probe, cancellationToken);
    }
}
