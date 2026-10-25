using Avalonia.Controls;

namespace AiCeo.Desktop.Views;

/// <summary>
/// v1.0.13: makes a shell window actually appear.
///
/// The failure this fixes is an app that keeps running — taskbar button and all — with no window on
/// screen. Three states produce exactly that, and all three are normalised here before the window is
/// shown:
/// <list type="number">
///   <item><see cref="WindowState.FullScreen"/> in XAML: a known Avalonia problem on some Windows
///   configurations, where the window is created but never becomes visible;</item>
///   <item><see cref="WindowState.Minimized"/>: the window only ever exists as a taskbar entry;</item>
///   <item>a window that was never shown at all because the application lifetime's own
///   <c>ShowMainWindow()</c> call was the only thing that could have done it.</item>
/// </list>
/// Presentation is cosmetic, so nothing in here is allowed to throw into the caller.
/// </summary>
internal static class WindowPresentation
{
    /// <summary>
    /// Shows and activates <paramref name="window"/> exactly once, from a normal state, in the
    /// taskbar and not pinned above every other application.
    /// </summary>
    /// <param name="window">The shell window to present.</param>
    /// <param name="origin">Where the call came from, recorded in <c>startup.log</c>.</param>
    /// <returns>True when the window is visible afterwards, so a failed attempt can be retried.</returns>
    internal static bool Present(Window window, string origin)
    {
        try
        {
            // FullScreen is the state that breaks visibility, Minimized is the one that only leaves a
            // taskbar button. Normal first, then show.
            if (window.WindowState is WindowState.FullScreen or WindowState.Minimized)
            {
                Program.Trace($"present ({origin}): resetting {window.GetType().Name} from {window.WindowState} to Normal");
                window.WindowState = WindowState.Normal;
            }

            window.ShowInTaskbar = true;
            window.Topmost = false;

            if (!window.IsVisible)
            {
                window.Show();
            }

            window.Activate();

            Program.Trace(
                $"present ({origin}): {window.GetType().Name} visible={window.IsVisible} "
                + $"state={window.WindowState} taskbar={window.ShowInTaskbar} topmost={window.Topmost}");

            return window.IsVisible;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "window presentation");
            return false;
        }
    }
}
