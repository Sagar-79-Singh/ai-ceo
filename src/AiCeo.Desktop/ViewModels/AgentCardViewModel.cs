using System.Windows.Input;
using AiCeo.Models;

namespace AiCeo.Desktop.ViewModels;

/// <summary>One agent in the sidebar. Commands are owned by the card so the XAML stays simple.</summary>
public sealed class AgentCardViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isConfirmingDelete;

    public AgentCardViewModel(AgentView agent, MainViewModel owner)
    {
        Agent = agent;
        _owner = owner;

        EditCommand = new RelayCommand(() => owner.BeginEdit(this));
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        ConfirmDeleteCommand = new AsyncRelayCommand(() => owner.ConfirmDeleteAsync(this));
        MakeCeoCommand = new AsyncRelayCommand(() => owner.MakeCeoAsync(this));
    }

    public AgentView Agent { get; }

    public Guid Id => Agent.Id;

    public string Name => Agent.Name;

    public string Role => Agent.Role;

    public bool IsManager => Agent.IsManager;

    public bool CanUseTools => Agent.CanUseTools;

    public bool CanBecomeCeo => !Agent.IsManager;

    public string ProviderDisplayName => Agent.ProviderDisplayName;

    public string ModelId => Agent.ModelId;

    public string KeyText => Agent.ApiKeyMask is not null
        ? $"key {Agent.ApiKeyMask}"
        : $"key: {Agent.ApiKeySource}";

    public string EndpointText => Agent.EffectiveBaseUrl;

    public string DeleteQuestion => $"Delete {Name}?";

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set => SetProperty(ref _isConfirmingDelete, value);
    }

    public ICommand EditCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    public ICommand ConfirmDeleteCommand { get; }

    public ICommand MakeCeoCommand { get; }
}
