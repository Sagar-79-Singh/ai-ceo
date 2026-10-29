namespace AiCeo.Desktop.ViewModels;

/// <summary>What a feed line represents.</summary>
public enum FeedKind
{
    Plan,
    Delegation,
    ToolCall,
    ToolResult,
    Result,
    Error,
    Note,

    /// <summary>v2.0.0: a write or a command is waiting for the user's decision.</summary>
    Approval,

    /// <summary>v2.0.1: the user's own message (the right-hand bubble in the chat).</summary>
    User,

    /// <summary>v2.0.1: a CEO message in the chat (the left-hand bubble).</summary>
    Ceo
}

/// <summary>One rendered line of the execution feed.</summary>
public sealed class FeedItemViewModel : ObservableObject
{
    private bool _isPending;

    public FeedKind Kind { get; init; }

    public string AgentName { get; init; } = string.Empty;

    public string? ToolName { get; init; }

    /// <summary>
    /// v2.0.1: the CEO ↔ worker thread this line belongs to. Set on the delegation card, so the
    /// chat can offer "open the worker's conversation", and on its result.
    /// </summary>
    public Guid? ThreadId { get; init; }

    /// <summary>v2.0.1: for a delegation, the worker the user can click.</summary>
    public string? ThreadAgent { get; init; }

    public string Text { get; init; } = string.Empty;

    public bool Failed { get; init; }

    public IReadOnlyList<PlanStepViewModel> Steps { get; init; } = Array.Empty<PlanStepViewModel>();

    /// <summary>True while the CEO waits for this worker's result.</summary>
    public bool IsPending
    {
        get => _isPending;
        set
        {
            if (SetProperty(ref _isPending, value))
            {
                OnPropertyChanged(nameof(DelegationTitle));
            }
        }
    }

    public bool ShowPlan => Kind == FeedKind.Plan;

    public bool ShowDelegation => Kind == FeedKind.Delegation;

    public bool ShowTool => Kind is FeedKind.ToolCall or FeedKind.ToolResult;

    public bool ShowToolArgs => Kind == FeedKind.ToolCall;

    public bool ShowToolOutput => Kind == FeedKind.ToolResult;

    public bool ShowResult => Kind == FeedKind.Result;

    public bool ShowError => Kind == FeedKind.Error;

    public bool ShowNote => Kind == FeedKind.Note;

    public bool ShowApproval => Kind == FeedKind.Approval;

    public bool ShowUser => Kind == FeedKind.User;

    public bool ShowCeo => Kind == FeedKind.Ceo;

    /// <summary>True when the chat can offer to open the worker's own conversation.</summary>
    public bool HasThread => ThreadId is not null;

    public string ThreadAgentName => ThreadAgent ?? AgentName;

    public bool HasSteps => Steps.Count > 0;

    public bool ShowPlanRaw => Kind == FeedKind.Plan && Steps.Count == 0;

    public string DelegationTitle => IsPending
        ? $"[{AgentName}] is thinking…"
        : $"[{AgentName}] was delegated";

    public string ToolStateText => Kind switch
    {
        FeedKind.ToolCall => AgentName,
        FeedKind.ToolResult => Failed ? "refused / failed" : "result",
        FeedKind.Approval => "waiting for your approval",
        FeedKind.Ceo => "CEO",
        _ => string.Empty
    };

    public string HeaderText => Kind switch
    {
        FeedKind.Plan => "CEO plan",
        FeedKind.Result => "worker result",
        FeedKind.Approval => "approval",
        _ => AgentName
    };
}

/// <summary>One numbered step of the CEO plan.</summary>
public sealed class PlanStepViewModel
{
    public PlanStepViewModel(int index, string role, string instructions)
    {
        Index = index;
        Role = role;
        Instructions = instructions;
    }

    public int Index { get; }

    public string Role { get; }

    public string Instructions { get; }
}
