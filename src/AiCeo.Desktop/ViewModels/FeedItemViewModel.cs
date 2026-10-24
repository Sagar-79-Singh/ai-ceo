using AiCeo.Desktop.Theme;
using AiCeo.Models;
using Avalonia.Media;

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
    Note
}

/// <summary>One rendered line of the execution feed.</summary>
public sealed class FeedItemViewModel : ObservableObject
{
    private bool _isPending;

    public FeedKind Kind { get; init; }

    public string AgentName { get; init; } = string.Empty;

    public string? ToolName { get; init; }

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
                OnPropertyChanged(nameof(DelegationTitle), nameof(PendingDotBrush));
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

    public bool HasSteps => Steps.Count > 0;

    public bool ShowPlanRaw => Kind == FeedKind.Plan && Steps.Count == 0;

    public string DelegationTitle => IsPending
        ? $"[{AgentName}] is thinking…"
        : $"[{AgentName}] was delegated";

    public string ToolStateText => Kind switch
    {
        FeedKind.ToolCall => AgentName,
        FeedKind.ToolResult => Failed ? "refused / failed" : "result",
        _ => string.Empty
    };

    public IBrush ToolOutputBrush => Failed
        ? ThemeBrushes.Get(UiTheme.Danger, "#FFB3B3")
        : ThemeBrushes.Get(UiTheme.TextBody, "#C7D6E8");

    public IBrush PendingDotBrush => IsPending
        ? ThemeBrushes.Get(UiTheme.Warn, "#FFC46B")
        : ThemeBrushes.Get(UiTheme.Ok, "#5EE0B0");

    /// <summary>Re-reads the themed brushes after a theme change.</summary>
    public void RefreshTheme() => OnPropertyChanged(nameof(ToolOutputBrush), nameof(PendingDotBrush));

    public string HeaderText => Kind switch
    {
        FeedKind.Plan => "CEO plan",
        FeedKind.Result => "worker result",
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
