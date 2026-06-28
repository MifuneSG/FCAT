using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FCAT.Models;
using FCAT.Services;

namespace FCAT.ViewModels;

/// <summary>
/// Standalone page for the session alert feed — every alert raised this session
/// (tackle, cap-out, boost-lost, logi-chain). Bound straight to the app-lifetime
/// <see cref="AlertHub"/>, so it shows the same feed as the fleet view's ALERTS panel
/// and the overlay, available whether or not a fleet is currently being monitored.
/// </summary>
public partial class AlertsViewModel : ObservableObject
{
    public AlertHub Hub { get; }
    public ObservableCollection<FcAlert> Alerts => Hub.Alerts;

    public AlertsViewModel(AlertHub hub) => Hub = hub;
}
