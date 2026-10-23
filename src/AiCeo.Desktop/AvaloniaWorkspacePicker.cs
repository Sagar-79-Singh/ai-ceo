using AiCeo.Services;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AiCeo.Desktop;

/// <summary>
/// "Select Workspace Folder" for Avalonia: the platform's own folder chooser (Win32 dialog on
/// Windows, GTK/Qt portal on Linux, NSOpenPanel on macOS).
/// </summary>
internal sealed class AvaloniaWorkspacePicker : IWorkspacePicker
{
    private readonly Func<Window?> _windowAccessor;

    internal AvaloniaWorkspacePicker(Func<Window?> windowAccessor)
    {
        _windowAccessor = windowAccessor;
    }

    public async Task<FolderPickResult> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var window = _windowAccessor();
            var storage = window?.StorageProvider;

            if (storage is null)
            {
                return FolderPickResult.Failed("The app window is not ready yet. Try again in a moment.");
            }

            if (!storage.CanPickFolder)
            {
                return FolderPickResult.Failed("This desktop does not provide a folder picker.");
            }

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the workspace folder agents may use",
                AllowMultiple = false
            });

            if (folders.Count == 0)
            {
                return FolderPickResult.Cancelled;
            }

            var path = folders[0].TryGetLocalPath();

            if (string.IsNullOrWhiteSpace(path))
            {
                return FolderPickResult.Failed("That folder has no local path (only local folders can be a workspace).");
            }

            return FolderPickResult.Picked(path);
        }
        catch (Exception exception)
        {
            return FolderPickResult.Failed($"Could not open the folder picker: {exception.Message}");
        }
    }
}
