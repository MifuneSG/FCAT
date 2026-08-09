using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// App-lifetime home for the alert feed and the on-screen overlay state.
/// Alerts and the overlay used to live on the per-page FleetViewModel, so navigating away from
/// the fleet view tore them down. Keeping them here means the overlay stays up and alerts keep
/// flowing no matter which FCAT page is showing - the fleet session just pushes alerts in via
/// <see cref="Raise"/>, and the overlay window (owned by MainWindow) binds to <see cref="Alerts"/>.
/// </summary>
public partial class AlertHub : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SessionLog _sessionLog;

    public AlertHub(SettingsService settings, SessionLog sessionLog)
    {
        _settings = settings;
        _sessionLog = sessionLog;
        _overlayEnabled = settings.Current.OverlayEnabled;
        _overlayLocked = settings.Current.OverlayLocked;
    }

    /// <summary>Persistent session feed for the in-app panels. Only <see cref="OverlayAlerts"/> is
    /// auto-cleared by the timeout; this list keeps everything until the session ends.</summary>
    public ObservableCollection<FcAlert> Alerts { get; } = [];

    /// <summary>Feed for the on-screen overlay - the same alerts, but the AlertClearSeconds timeout
    /// expires them here so the overlay stays tidy over the game.</summary>
    public ObservableCollection<FcAlert> OverlayAlerts { get; } = [];

    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private bool _overlayLocked;

    /// <summary>Unread count for the nav badge; reset by <see cref="MarkRead"/>.</summary>
    [ObservableProperty] private int _unreadCount;

    public void MarkRead() => UnreadCount = 0;

    public double OverlayLeft => _settings.Current.OverlayLeft;
    public double OverlayTop  => _settings.Current.OverlayTop;

    [RelayCommand] private void ToggleOverlay()     => OverlayEnabled = !OverlayEnabled;
    [RelayCommand] private void ToggleOverlayLock() => OverlayLocked  = !OverlayLocked;

    /// <summary>Called by the overlay host when the window moves / locks, so it survives sessions.</summary>
    public void PersistOverlay(double left, double top, bool locked)
    {
        _settings.Current.OverlayLeft   = left;
        _settings.Current.OverlayTop    = top;
        _settings.Current.OverlayLocked = locked;
        _settings.Save();
    }

    /// <summary>Inserts an alert at the top of the feed, plays its sound, and schedules auto-clear.
    /// Must be called on the UI thread.</summary>
    public void Raise(FcAlert alert)
    {
        Alerts.Insert(0, alert);
        while (Alerts.Count > 100)
            Alerts.RemoveAt(Alerts.Count - 1);

        OverlayAlerts.Insert(0, alert);
        while (OverlayAlerts.Count > 100)
            OverlayAlerts.RemoveAt(OverlayAlerts.Count - 1);

        UnreadCount++;

        // Permanent AAR record - the live feeds are bounded/auto-cleared, this isn't.
        var line = string.IsNullOrEmpty(alert.SubText) ? alert.Headline : $"{alert.Headline}: {alert.SubText}";
        _sessionLog.Record(alert.AlertTag, line);

        var cfg = _settings.Current;
        if (cfg.AlertSoundsEnabled && !cfg.MutedAlertTypes.Contains(alert.AlertType.ToString()))
        {
            var preset = SoundFor(alert);
            // Throttle per type so repeated alerts don't machine-gun the speaker.
            var gap = TimeSpan.FromSeconds(Math.Max(0, cfg.AlertSoundThrottleSeconds));
            SoundService.PlayThrottled(preset, SoundKey(alert), gap);
        }

        // Louder alerts stay on the overlay longer.
        var clearSecs = alert.Severity switch
        {
            AlertSeverity.Critical => cfg.AlertClearSecondsCritical,
            AlertSeverity.Info     => cfg.AlertClearSecondsInfo,
            _                      => cfg.AlertClearSeconds,
        };
        if (clearSecs > 0) _ = ExpireAlertAsync(alert, clearSecs);
    }

    /// <summary>Custom rules carry their own sound; built-ins use their per-type setting.</summary>
    private string SoundFor(FcAlert alert)
    {
        if (alert.AlertType == AlertType.Custom)
            return string.IsNullOrWhiteSpace(alert.CustomSound) ? SeverityDefault(alert.Severity) : alert.CustomSound;

        var cfg = _settings.Current;
        return alert.AlertType switch
        {
            AlertType.Tackled      => cfg.TackledSound,
            AlertType.CapTrouble   => cfg.CapTroubleSound,
            AlertType.BoostLost    => cfg.BoostLostSound,
            AlertType.LogiChain    => cfg.LogiChainSound,
            AlertType.DpsLoss      => cfg.DpsLossSound,
            AlertType.LogiRatio    => cfg.LogiRatioSound,
            AlertType.IntelHostile => cfg.IntelHostileSound,
            _                      => "None",
        };
    }

    private static string SeverityDefault(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "Alarm",
        AlertSeverity.Warning  => "Beep",
        _                      => "None",
    };

    // Custom rules throttle per rule, not per type, so two different rules don't mute each other.
    private static string SoundKey(FcAlert alert) =>
        alert.AlertType == AlertType.Custom && alert.CustomTag.Length > 0
            ? $"Custom:{alert.CustomTag}"
            : alert.AlertType.ToString();

    private async Task ExpireAlertAsync(FcAlert alert, int seconds)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds)); } catch { return; }
        // Clear from the overlay only - the in-app Alerts list keeps the full session history.
        App.Current.Dispatcher.Invoke(() => OverlayAlerts.Remove(alert));
    }

    partial void OnOverlayEnabledChanged(bool value)
    {
        _settings.Current.OverlayEnabled = value;
        _settings.Save();
    }

    partial void OnOverlayLockedChanged(bool value)
    {
        _settings.Current.OverlayLocked = value;
        _settings.Save();
    }
}
