using System.Text.RegularExpressions;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Evaluates the FC's own alert rules against the two text feeds EVE actually exposes: the intel
/// channel (chatlogs) and your character's gamelog. A rule that matches raises a Custom alert with
/// whatever name, severity and sound the FC gave it.
/// </summary>
public class CustomAlertService
{
    private readonly SettingsService _settings;
    private readonly AlertHub _hub;

    // Last fire time per rule id, so a repeating intel line doesn't spam the feed.
    private readonly Dictionary<string, DateTime> _lastFired = [];

    // Compiled patterns, rebuilt when a rule's pattern changes.
    private readonly Dictionary<string, (string pattern, Regex regex)> _regexCache = [];

    public CustomAlertService(SettingsService settings, AlertHub hub)
    {
        _settings = settings;
        _hub = hub;
    }

    public void OnIntelMessage(string message) => Evaluate(AlertRuleSource.IntelChannel, message);
    public void OnGameLogLine(string line)     => Evaluate(AlertRuleSource.GameLog, line);

    private void Evaluate(AlertRuleSource source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var rule in _settings.Current.CustomAlerts)
        {
            if (!rule.Enabled || rule.Source != source) continue;
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            if (!Matches(rule, text)) continue;

            var gap = TimeSpan.FromSeconds(Math.Max(0, rule.ThrottleSeconds));
            if (_lastFired.TryGetValue(rule.Id, out var last) && DateTime.UtcNow - last < gap) continue;
            _lastFired[rule.Id] = DateTime.UtcNow;

            App.Current.Dispatcher.Invoke(() => _hub.Raise(new FcAlert
            {
                Timestamp        = DateTime.Now,
                AlertType        = AlertType.Custom,
                CustomTag        = rule.Name,
                CustomHeadline   = rule.Headline,
                CustomSound      = rule.Sound,
                SeverityOverride = rule.Severity,
                Detail           = Trim(text),
                RawLogLine       = text,
            }));
        }
    }

    private bool Matches(CustomAlertRule rule, string text)
    {
        if (rule.Match == AlertRuleMatch.Contains)
            return text.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);

        var rx = GetRegex(rule);
        return rx != null && rx.IsMatch(text);
    }

    /// <summary>Compiles (and caches) a rule's regex. A bad pattern just never matches - the rule
    /// editor validates on save, this is the belt-and-braces for a hand-edited settings file.</summary>
    private Regex? GetRegex(CustomAlertRule rule)
    {
        if (_regexCache.TryGetValue(rule.Id, out var hit) && hit.pattern == rule.Pattern)
            return hit.regex;
        try
        {
            var rx = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                               TimeSpan.FromMilliseconds(100));
            _regexCache[rule.Id] = (rule.Pattern, rx);
            return rx;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>True if the pattern is usable - used by the rule editor to flag a bad regex.</summary>
    public static bool IsValidPattern(AlertRuleMatch match, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (match == AlertRuleMatch.Contains) return true;
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string Trim(string s) => s.Length <= 140 ? s : s[..137] + "...";
}
