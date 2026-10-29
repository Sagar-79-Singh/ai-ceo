using AiCeo.Models;
using Microsoft.Extensions.Logging;

namespace AiCeo.Services;

/// <summary>
/// v2.0.1 — runs one device-flow sign-in in the background and publishes what the page should show.
///
/// The flow is deliberately not awaited by the caller: the UI opens a modal on
/// <see cref="State"/>, the user walks to their browser, and the page repaints whenever
/// <see cref="Changed"/> fires. On success the token is written to the <c>UserSettings</c> row (as the
/// provider key) and <see cref="LlmSetupProvider"/> publishes it, so the very next run is authorised
/// without a restart.
/// </summary>
public sealed class DeviceFlowCoordinator
{
    private readonly DeviceFlowService _service;
    private readonly LlmSetupProvider _connection;
    private readonly ILogger<DeviceFlowCoordinator>? _logger;

    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private DeviceFlowSnapshot _state = DeviceFlowSnapshot.Idle;

    public DeviceFlowCoordinator(
        DeviceFlowService service,
        LlmSetupProvider connection,
        ILogger<DeviceFlowCoordinator>? logger = null)
    {
        _service = service;
        _connection = connection;
        _logger = logger;
    }

    /// <summary>Raised whenever <see cref="State"/> changes, from whatever thread made the change.</summary>
    public event Action? Changed;

    /// <summary>What the sign-in modal renders. Never null.</summary>
    public DeviceFlowSnapshot State => Volatile.Read(ref _state);

    /// <summary>True while a flow is running (and has not been cancelled).</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _cts is not null;
            }
        }
    }

    /// <summary>
    /// Starts a sign-in. Returns false when one is already running or the method is a plain API key.
    /// The work continues on the thread pool; watch <see cref="Changed"/> for the result.
    /// </summary>
    public bool Start(AuthMethod method)
    {
        if (!DeviceFlowService.Supports(method))
        {
            return false;
        }

        CancellationTokenSource cts;

        lock (_gate)
        {
            if (_cts is not null)
            {
                return false;
            }

            cts = new CancellationTokenSource();
            _cts = cts;
        }

        Publish(new DeviceFlowSnapshot(
            method,
            DeviceFlowStatus.Waiting,
            null,
            null,
            "Requesting a sign-in code…",
            null));

        _ = Task.Run(() => RunAsync(method, cts), CancellationToken.None);
        return true;
    }

    /// <summary>Aborts the running flow. The modal closes and nothing is stored.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// Forgets a device-flow token. The model is kept, so the settings screen stays usable while the
    /// user decides how to sign in again.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _connection.SignOutAsync(cancellationToken).ConfigureAwait(false);
        Publish(DeviceFlowSnapshot.Idle);
    }

    /// <summary>Closes a finished sign-in modal without touching the stored connection.</summary>
    public void Dismiss()
    {
        if (!IsActive)
        {
            Publish(DeviceFlowSnapshot.Idle);
        }
    }

    private async Task RunAsync(AuthMethod method, CancellationTokenSource cts)
    {
        var token = cts.Token;

        try
        {
            var grant = await _service.StartAsync(method, token).ConfigureAwait(false);

            Publish(new DeviceFlowSnapshot(
                method,
                DeviceFlowStatus.Waiting,
                grant.UserCode,
                grant.VerificationUri,
                $"Enter the code at {grant.VerificationUri} and authorise this device.",
                null));

            var result = await _service.WaitForTokenAsync(grant, token).ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.AccessToken))
            {
                Publish(new DeviceFlowSnapshot(
                    method,
                    DeviceFlowStatus.Failed,
                    grant.UserCode,
                    grant.VerificationUri,
                    result.Message ?? "The sign-in did not complete.",
                    null));

                return;
            }

            var stored = await StoreAsync(method, result).ConfigureAwait(false);

            Publish(new DeviceFlowSnapshot(
                method,
                stored ? DeviceFlowStatus.Complete : DeviceFlowStatus.Failed,
                grant.UserCode,
                grant.VerificationUri,
                stored
                    ? "Signed in. The token is stored in the local database and used as the provider key."
                    : "The token was issued but could not be stored.",
                result.Login));
        }
        catch (OperationCanceledException)
        {
            Publish(DeviceFlowSnapshot.Idle);
        }
        catch (DeviceFlowException exception)
        {
            _logger?.LogWarning(exception, "Device flow for {Method} failed", method);
            Publish(new DeviceFlowSnapshot(method, DeviceFlowStatus.Failed, null, null, exception.Message, null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogError(exception, "Device flow for {Method} crashed", method);
            Publish(new DeviceFlowSnapshot(
                method,
                DeviceFlowStatus.Failed,
                null,
                null,
                "The sign-in failed: " + exception.Message,
                null));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Writes the token as the provider key, keeping a model and base URL the user already chose.
    /// The provider itself is untouched: the token is used exactly like a pasted key.
    /// </summary>
    private async Task<bool> StoreAsync(AuthMethod method, DeviceAuthResult result)
    {
        var current = _connection.Current;

        var model = current.HasModel ? current.ModelId : AuthMethodCatalog.SuggestedModelFor(method);

        var setup = new LlmSetup(current.Provider, current.BaseUrl, result.AccessToken, model);
        var auth = new LlmAuthState(method, result.Login, DateTime.UtcNow);

        await _connection.SaveSignedInAsync(setup, auth).ConfigureAwait(false);
        return true;
    }

    private void Publish(DeviceFlowSnapshot snapshot)
    {
        Volatile.Write(ref _state, snapshot);

        var handler = Changed;

        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogWarning(exception, "A device-flow listener threw");
        }
    }
}
