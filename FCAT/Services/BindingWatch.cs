using System.Diagnostics;
using System.Windows.Diagnostics;

namespace FCAT.Services;

/// <summary>
/// Reports broken XAML bindings into FCAT's own log.
///
/// A binding that does not resolve is WPF's quietest failure: the element still renders, the build
/// is green, the XAML parses, and the app launches clean. A typo'd property shows an empty string.
/// A Command that resolves to null leaves a button that looks and feels alive and does nothing when
/// you press it. Both have already happened here, and the second one nearly shipped.
///
/// WPF does say so, but only down its own trace plumbing, which nothing was listening to. Now it
/// lands in the session log next to everything else, so "that button does nothing" is one Copy
/// diagnostics away from naming the property and the file.
///
/// This is on in every build, not just Debug. It is a handful of strings on a path that should
/// never fire, and the whole point is to catch the one that fires on someone else's machine.
/// </summary>
public static class BindingWatch
{
    public static void Start()
    {
        try
        {
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(new LogListener());
            source.Switch.Level = SourceLevels.Warning;   // errors and warnings, not the chatter
        }
        catch (Exception ex)
        {
            Log.Warn("binding", "could not attach the binding watcher", ex);
        }
    }

    private sealed class LogListener : TraceListener
    {
        // WPF hands a binding failure over in fragments. Only the completed line is worth a record.
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Log.Warn("binding", message.Trim());
        }
    }
}
