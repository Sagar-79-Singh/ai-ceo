namespace AiCeo.Services;

/// <summary>Result of asking the user for a folder.</summary>
public sealed record FolderPickResult(bool Selected, string? Path, string? Error)
{
    public static FolderPickResult Cancelled => new(false, null, null);

    public static FolderPickResult Picked(string path) => new(true, path, null);

    public static FolderPickResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Implemented by the desktop shell (MAUI uses the native Windows folder picker). The core and
/// the tests use <see cref="ManualWorkspacePicker"/> so nothing here depends on a UI toolkit.
/// </summary>
public interface IWorkspacePicker
{
    Task<FolderPickResult> PickFolderAsync(CancellationToken cancellationToken = default);
}

/// <summary>Setter-based picker used by tests and by any non-interactive host.</summary>
public sealed class ManualWorkspacePicker : IWorkspacePicker
{
    public string? NextFolder { get; set; }

    public FolderPickResult? NextResult { get; set; }

    public Task<FolderPickResult> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        if (NextResult is not null)
        {
            var result = NextResult;
            NextResult = null;
            return Task.FromResult(result);
        }

        return Task.FromResult(
            string.IsNullOrWhiteSpace(NextFolder)
                ? FolderPickResult.Cancelled
                : FolderPickResult.Picked(NextFolder!));
    }
}
