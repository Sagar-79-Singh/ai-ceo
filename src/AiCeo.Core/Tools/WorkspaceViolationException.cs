namespace AiCeo.Tools;

/// <summary>
/// Thrown whenever a tool call would reach outside the selected workspace folder. This is the
/// hard guardrail: callers must never hand the path to the file system after this is thrown.
/// </summary>
public sealed class WorkspaceViolationException : Exception
{
    public WorkspaceViolationException(string message) : base(message)
    {
    }

    public static WorkspaceViolationException NoWorkspace()
        => new("No workspace folder is selected. Choose one with \"Select Workspace Folder\" before using file or terminal tools.");

    public static WorkspaceViolationException Outside(string attemptedPath, string workspaceRoot)
        => new($"Access denied: '{attemptedPath}' is outside the workspace folder '{workspaceRoot}'. "
            + "File and terminal tools may only touch files inside the selected workspace.");
}
