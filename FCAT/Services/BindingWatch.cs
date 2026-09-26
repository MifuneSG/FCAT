using System.Diagnostics;
using System.Text.RegularExpressions;
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
public static partial class BindingWatch
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
        // Each distinct failure is worth one line, not one per row.
        //
        // WPF reports a binding failure the same way whether it is permanent or transient, and
        // several idioms fail on their first evaluation and resolve a moment later once the element
        // is in the tree. The context-menu "Tag" pattern does exactly that, once per row: opening
        // five pages produced 238 identical warnings, which would bury the one that matters. So the
        // first of each distinct message goes in the log and its repeats do not.
        private readonly HashSet<string> _seen = [];

        // WPF hands a binding failure over in fragments. Only the completed line is worth a record.
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var text = message.Trim();
            lock (_seen)
            {
                // DataItem names the specific row, so drop it when deciding what counts as distinct.
                var key = DataItemPart().Replace(text, string.Empty);
                if (!_seen.Add(key)) return;
            }
            Log.Warn("binding", text);
        }
    }

    [GeneratedRegex(@"DataItem=[^;]*;")]
    private static partial Regex DataItemPart();
}
