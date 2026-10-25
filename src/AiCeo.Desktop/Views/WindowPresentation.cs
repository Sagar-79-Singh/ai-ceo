using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiCeo.Desktop.Views;

/// <summary>
/// Makes sure a shell window is actually on screen.
///
/// v1.0.11: an installed copy could end up running with a taskbar button but no visible window.
/// Three things cause that on Windows, and all three are neutralised here:
/// <list type="number">
///   <item>the window is shown while still marked <see cref="WindowState.Minimized"/>, so it only
///   ever appears in the taskbar;</item>
///   <item>the requested size is larger than the screen (a 1480×940 DIP window on a 1366×768 laptop
///   is mostly off-screen, and Centering it puts the title bar outside the visible area);</item>
///   <item>the window is created correctly but the shell steals focus before it paints, leaving it
///   behind another window.</item>
/// </list>
/// Every step is defensive: presentation is cosmetic and must never be able to kill the process.
/// </summary>
internal static class WindowPresentation
{
    /// <summary>Pixels kept clear around the window when it has to be shrunk to fit the screen.</summary>
    private const double Margin = 24;

    /// <summary>
    /// Called once the platform window exists: restore it from the taskbar, fit it to the monitor's
    /// working area, show it and bring it to the front.
    /// </summary>
    internal static void PrepareForDisplay(Window window)
    {
        try
        {
            // A window whose state is Minimized never paints; the taskbar button is all the user
            // sees. Normalise before anything else.
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            FitToWorkingArea(window);

            window.ShowInTaskbar = true;

            if (!window.IsVisible)
            {
                window.Show();
            }

            window.Activate();

            Program.Trace(
                $"presentation: {window.GetType().Name} visible={window.IsVisible} state={window.WindowState} "
                + $"size={window.Width:0}x{window.Height:0} pos={window.Position} taskbar={window.ShowInTaskbar}");

            // The first Activate() can be swallowed while the shell is still starting the process.
            // One more attempt after the first frame is enough, and it is harmless if it is not.
            Dispatcher.UIThread.Post(() => Nudge(window), DispatcherPriority.Background);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "window presentation");
        }
    }

    /// <summary>
    /// Second, deferred attempt at bringing the window to the front. If it is still invisible after
    /// the first frame — a genuinely hidden window — that is logged, because it is a bug worth
    /// seeing in <c>startup.log</c>.
    /// </summary>
    private static void Nudge(Window window)
    {
        try
        {
            if (!window.IsVisible)
            {
                Program.Trace($"presentation: {window.GetType().Name} was still hidden; showing it again");
                window.Show();
            }

            window.Activate();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "window presentation (deferred)");
        }
    }

    /// <summary>
    /// Shrinks the window so it fits the monitor it will open on. Centering a window that is bigger
    /// than the screen leaves the title bar — and therefore any way to move or close it — outside the
    /// visible desktop, which reads to the user as "the app did not open".
    /// </summary>
    private static void FitToWorkingArea(Window window)
    {
        Screen? screen;

        try
        {
            screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return;
        }

        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var workingWidth = screen.WorkingArea.Width / scaling;
        var workingHeight = screen.WorkingArea.Height / scaling;

        var width = Fit(window.Width, workingWidth, window.MinWidth);
        var height = Fit(window.Height, workingHeight, window.MinHeight);

        if (Math.Abs(width - window.Width) > 0.5 || Math.Abs(height - window.Height) > 0.5)
        {
            Program.Trace(
                $"presentation: {window.GetType().Name} {window.Width:0}x{window.Height:0} is larger than the "
                + $"{workingWidth:0}x{workingHeight:0} working area; resizing to {width:0}x{height:0}");

            window.Width = width;
            window.Height = height;

            // Re-centre: WindowStartupLocation is honoured on the first show only, and a window that
            // was created oversized may already have been positioned off the visible desktop.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private static double Fit(double requested, double available, double minimum)
    {
        // An auto-sized window (NaN) has no requested size to clamp: leave it alone.
        if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0 || available <= 0)
        {
            return requested;
        }

        if (requested <= available)
        {
            return requested;
        }

        // Never go below the declared minimum, but never exceed the screen either: on a very small
        // display the minimum wins only if it still leaves a usable window.
        var target = Math.Max(available - Margin, Math.Min(minimum, available));
        return target > 0 ? target : requested;
    }
}
