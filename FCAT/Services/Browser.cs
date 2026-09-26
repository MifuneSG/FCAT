using System.Diagnostics;

namespace FCAT.Services;

/// <summary>
/// Opening a link in the user's browser.
///
/// Seven call sites used to do this inline, with four different ideas about what happens when it
/// fails: two identical private helpers that swallowed the error, two inline swallows, two that
/// set a status message, and one unguarded call in the login flow that could take the window down
/// on a machine with no default browser. Every one of them was the same four lines.
///
/// One way now. It never throws, it always says so in the log, and it hands back a message for the
/// panels that show one. A browser that will not open is never worth taking a page down over, but
/// it is always worth being able to see afterwards.
/// </summary>
public static class Browser
{
    /// <summary>Opens <paramref name="url"/>. False when it could not, with why in <paramref name="error"/>.</summary>
    public static bool Open(string? url, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) { error = "no link to open"; return false; }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn("browser", $"could not open {url}", ex);
            return false;
        }
    }

    /// <summary>Opens <paramref name="url"/> where nothing is listening for the failure.</summary>
    public static bool Open(string? url) => Open(url, out _);
}
