using System.Windows.Input;
using AiCeo.Models;

namespace AiCeo.Desktop.ViewModels;

/// <summary>The create/edit agent form, shown as an in-window overlay.</summary>
public sealed class AgentEditorViewModel : ObservableObject
{
    private readonly Func<AgentFormModel, Task> _save;
    private readonly Action _close;

    private string _name = string.Empty;
    private string _role = string.Empty;
    private string _systemPrompt = string.Empty;
    private string _modelId = string.Empty;
    private string? _baseUrl;
    private string? _apiKey;
    private bool _clearApiKey;
    private bool _canUseTools;
    private bool _isBusy;
    private string? _validationError;
    private LlmProviderInfo _selectedProvider;

    public AgentEditorViewModel(
        AgentView? agent,
        IReadOnlyList<LlmProviderInfo> providers,
        string? workspacePath,
        Func<AgentFormModel, Task> save,
        Action close)
    {
        _save = save;
        _close = close;
        _owner = agent;

        Providers = providers;
        WorkspacePath = workspacePath;

        _selectedProvider = providers.FirstOrDefault(info => info.Provider == (agent?.Provider ?? LlmProvider.OmniRoute))
                            ?? providers[0];

        if (agent is not null)
        {
            Id = agent.Id;
            _name = agent.Name;
            _role = agent.Role;
            _systemPrompt = agent.SystemPrompt;
            _modelId = agent.ModelId;
            _baseUrl = agent.BaseUrl;
            _canUseTools = agent.CanUseTools;
        }
        else
        {
            _modelId = _selectedProvider.DefaultModel;
        }

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _close());
    }

    private readonly AgentView? _owner;

    public Guid? Id { get; private set; }

    public bool IsEdit => Id is not null;

    public string Title => IsEdit ? "Edit agent" : "Create agent";

    public string Subtitle => "Agents in the star topology. Workers never talk to each other.";

    public string SubmitText => IsEdit ? "Save changes" : "Create agent";

    /// <summary>The manager never gets tools, so the checkbox is disabled while editing it.</summary>
    public bool IsCeo => _owner?.IsManager == true;

    public bool IsNotCeo => !IsCeo;

    public IReadOnlyList<LlmProviderInfo> Providers { get; }

    public string? WorkspacePath { get; }

    public LlmProviderInfo SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is null || !SetProperty(ref _selectedProvider, value))
            {
                return;
            }

            // Keep the model id sane when the backend changes.
            if (string.IsNullOrWhiteSpace(ModelId)
                || Providers.Any(info => string.Equals(info.DefaultModel, ModelId, StringComparison.OrdinalIgnoreCase)))
            {
                ModelId = value.DefaultModel;
            }

            OnPropertyChanged(nameof(ProviderHint), nameof(BaseUrlPlaceholder), nameof(ApiKeyPlaceholder), nameof(ShowClearKey));
        }
    }

    public string ProviderHint => SelectedProvider.Hint;

    public string BaseUrlPlaceholder => string.IsNullOrWhiteSpace(SelectedProvider.DefaultBaseUrl)
        ? "https://your-server/v1"
        : SelectedProvider.DefaultBaseUrl;

    public string ApiKeyPlaceholder => IsEdit && _owner?.HasStoredApiKey == true
        ? "stored key — leave blank to keep"
        : SelectedProvider.RequiresApiKey
            ? $"required, or set {SelectedProvider.ApiKeyEnvironmentVariable}"
            : $"optional, or set {SelectedProvider.ApiKeyEnvironmentVariable}";

    public bool ShowClearKey => IsEdit && _owner?.HasStoredApiKey == true;

    public string ClearKeyLabel => $"Remove the stored key (currently {_owner?.ApiKeyMask})";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Role
    {
        get => _role;
        set => SetProperty(ref _role, value);
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetProperty(ref _systemPrompt, value);
    }

    public string ModelId
    {
        get => _modelId;
        set => SetProperty(ref _modelId, value);
    }

    public string? BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string? ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                OnPropertyChanged(nameof(HasNewKey));
            }
        }
    }

    public bool HasNewKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool ClearApiKey
    {
        get => _clearApiKey;
        set => SetProperty(ref _clearApiKey, value);
    }

    public bool CanUseTools
    {
        get => _canUseTools;
        set
        {
            if (SetProperty(ref _canUseTools, value))
            {
                OnPropertyChanged(nameof(ToolsHint));
            }
        }
    }

    public string ToolsHint => IsCeo
        ? "The CEO coordinates only — tool access always belongs to a worker, never to the manager."
        : string.IsNullOrWhiteSpace(WorkspacePath)
            ? "No workspace folder is selected, so tools stay off until you pick one in the sidebar."
            : $"Confined to {WorkspacePath}. Paths outside that folder are refused.";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ValidationError = null;

        var name = Name.Trim();
        var role = Role.Trim();
        var systemPrompt = SystemPrompt.Trim();

        if (name.Length == 0)
        {
            ValidationError = "Name is required.";
            return;
        }

        if (role.Length == 0)
        {
            ValidationError = "Role is required.";
            return;
        }

        if (systemPrompt.Length == 0)
        {
            ValidationError = "System prompt is required.";
            return;
        }

        if (SelectedProvider.Provider == LlmProvider.Custom && string.IsNullOrWhiteSpace(BaseUrl))
        {
            ValidationError = "The Custom provider requires a Base URL.";
            return;
        }

        IsBusy = true;

        try
        {
            await _save(new AgentFormModel
            {
                Id = Id,
                Name = name,
                Role = role,
                SystemPrompt = systemPrompt,
                Provider = SelectedProvider.Provider,
                BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim(),
                ModelId = ModelId.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
                ClearApiKey = ClearApiKey && string.IsNullOrWhiteSpace(ApiKey),
                CanUseTools = CanUseTools && !IsCeo
            });
        }
        catch (Exception exception)
        {
            ValidationError = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
