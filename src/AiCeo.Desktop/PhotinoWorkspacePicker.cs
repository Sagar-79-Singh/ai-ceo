using AiCeo.Services;
using Photino.NET;

namespace AiCeo.Desktop;

/// <summary>
/// "Select Workspace Folder" for the PhotinoX shell: the platform's own folder chooser through
/// <see cref="PhotinoWindow.ShowOpenFolder"/> — the Win32 <c>IFileDialog</c> folder picker on
/// Windows, GTK's native chooser on Linux, <c>NSOpenPanel</c> on macOS. No managed dialog, no
/// dependency on a UI toolkit.
///
/// The call is marshalled onto the message-loop thread, because that is the thread that owns the
/// native window handle; the task completes when the user closes the dialog.
/// </summary>
internal sealed class PhotinoWorkspacePicker : IWorkspacePicker
{
    private const string DialogTitle = "Select the workspace folder agents may use";

    private readonly Func<PhotinoWindow?> _windowAccessor;

    internal PhotinoWorkspacePicker(Func<PhotinoWindow?> windowAccessor)
    {
        _windowAccessor = windowAccessor;
    }

    public Task<FolderPickResult> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FolderPickResult.Cancelled);
        }

        var window = _windowAccessor();

        if (window is null || !window.IsInitialized || window.IsClosed)
        {
            return Task.FromResult(FolderPickResult.Failed("The app window is not ready yet. Try again in a moment."));
        }

        // The dialog is modal and blocks until the user answers it, so it always runs where the
        // native window lives.
        if (UiThread.CheckAccess())
        {
            return Task.FromResult(Show(window));
        }

        var completion = new TaskCompletionSource<FolderPickResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!UiThread.TryPost(() => completion.TrySetResult(SafeShow(window))))
        {
            completion.TrySetResult(FolderPickResult.Failed("The window is closing, so the folder picker could not open."));
        }

        return completion.Task;
    }

    private static FolderPickResult SafeShow(PhotinoWindow window)
    {
        try
        {
            return Show(window);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FolderPickResult.Failed("Could not open the folder picker: " + exception.Message);
        }
    }

    private static FolderPickResult Show(PhotinoWindow window)
    {
        var paths = window.ShowOpenFolder(title: DialogTitle, defaultPath: null, multiSelect: false);

        if (paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0]))
        {
            return FolderPickResult.Cancelled;
        }

        var path = paths[0];

        if (!Path.IsPathFullyQualified(path))
        {
            return FolderPickResult.Failed("That folder has no local path (only local folders can be a workspace).");
        }

        return FolderPickResult.Picked(path);
    }
}
