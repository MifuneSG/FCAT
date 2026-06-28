using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// App-lifetime home for the alert feed and the on-screen overlay state.
///
/// Alerts and the overlay used to live on the per-page FleetViewModel, so navigating away from
/// the fleet view tore them down. Keeping them here means the overlay stays up and alerts keep
/// flowing no matter which FCAT page is showing — the fleet session just pushes alerts in via
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
        _overlayEnabled = settings.Current.OverlayEnabled;   // restore last-saved on/off state
        _overlayLocked = settings.Current.OverlayLocked;
    }

    /// <summary>Newest-first feed shared by the in-app panel and the overlay window.</summary>
    public ObservableCollection<FcAlert> Alerts { get; } = [];

    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private bool _overlayLocked;

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
        while (Alerts.Count > 100)            // keep the feed bounded
            Alerts.RemoveAt(Alerts.Count - 1);

        // Permanent record for the after-action log (alerts auto-clear from the live feed).
        var line = string.IsNullOrEmpty(alert.SubText) ? alert.Headline : $"{alert.Headline} — {alert.SubText}";
        _sessionLog.Record(alert.AlertTag, line);

        if (_settings.Current.AlertSoundsEnabled)
        {
            var preset = alert.AlertType switch
            {
                AlertType.Tackled    => _settings.Current.TackledSound,
                AlertType.CapTrouble => _settings.Current.CapTroubleSound,
                AlertType.BoostLost  => _settings.Current.BoostLostSound,
                AlertType.LogiChain  => _settings.Current.BoostLostSound,  // same "a key ship dropped" cue
                _                    => "None",
            };
            // Throttle per type so repeated alerts don't machine-gun the speaker.
            SoundService.PlayThrottled(preset, alert.AlertType.ToString(), TimeSpan.FromSeconds(2));
        }

        var clearSecs = _settings.Current.AlertClearSeconds;
        if (clearSecs > 0) _ = ExpireAlertAsync(alert, clearSecs);
    }

    private async Task ExpireAlertAsync(FcAlert alert, int seconds)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds)); } catch { return; }
        App.Current.Dispatcher.Invoke(() => Alerts.Remove(alert));
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
