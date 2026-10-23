using AiCeo.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Windows.Storage.Pickers;

namespace AiCeo.Maui;

/// <summary>
/// The native Windows folder picker behind "Select Workspace Folder".
///
/// The WinUI 3 picker must be created, initialised with the window handle and shown on the UI
/// thread, while Blazor event handlers run on the Blazor circuit — hence the
/// <see cref="MainThread"/> marshalling below.
/// </summary>
public sealed class LocalFolderPicker : IWorkspacePicker
{
    public Task<FolderPickResult> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var mauiWindow = Application.Current?.Windows.FirstOrDefault();
                if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
                {
                    return FolderPickResult.Failed("The app window is not ready yet. Try again in a moment.");
                }

                var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);

                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    CommitButtonText = "Use this folder"
                };

                picker.FileTypeFilter.Add("*");

                // Without this the dialog cannot be shown from a WinUI 3 desktop app.
                WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

                var folder = await picker.PickSingleFolderAsync();

                if (folder is null)
                {
                    return FolderPickResult.Cancelled;
                }

                return FolderPickResult.Picked(folder.Path);
            }
            catch (Exception exception)
            {
                return FolderPickResult.Failed($"Could not open the folder picker: {exception.Message}");
            }
        });
    }
}
