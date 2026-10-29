using System.Diagnostics;

namespace AiCeo.Desktop;

/// <summary>
/// v2.0.1 — opens the device-flow verification page in the user's own browser.
///
/// The sign-in hands the user a short code and a URL ("github.com/login/device"); typing that URL
/// by hand from a chat window is exactly the kind of friction that makes people give up and paste a
/// key instead, so the modal carries a button that opens it. Only absolute <c>https</c> URLs are
/// accepted — the value arrives from a provider's JSON, and it is never passed through a shell.
/// </summary>
internal static class LinkLauncher
{
    /// <summary>Opens <paramref name="url"/> with the OS handler. False when it could not be opened.</summary>
    internal static bool Open(string? url)
    {
        if (!Uri.TryCreate(url ?? string.Empty, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // ShellExecute is what hands the URL to the default browser on Windows; the page the
                // app is running on is a WebView, so there is no other way out of it.
                using var shell = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });

                return shell is not null;
            }

            var launcher = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open";

            using var process = Process.Start(new ProcessStartInfo(launcher)
            {
                ArgumentList = { uri.AbsoluteUri },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            return process is not null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No browser, no xdg-open, a locked-down desktop: the URL is printed in the modal, so the
            // user can still finish the sign-in by hand. Never let this break the flow.
            return false;
        }
    }
}
