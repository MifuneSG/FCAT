using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCAT.Services;

namespace FCAT.ViewModels;

public partial class MenuViewModel : ObservableObject
{
    private readonly EsiAuthService _auth;
    private readonly EsiService _esi;
    private readonly ShellViewModel _shell;

    public MenuViewModel(EsiAuthService auth, EsiService esi, ShellViewModel shell)
    {
        _auth = auth;
        _esi = esi;
        _shell = shell;

        CharacterName = auth.AuthenticatedCharacterName;
        PortraitUrl = $"https://images.evetech.net/characters/{auth.AuthenticatedCharacterId}/portrait?size=128";

        _ = LoadCharacterInfoAsync();
        _ = CheckFleetStatusAsync();
    }

    [ObservableProperty] private string _characterName = string.Empty;
    [ObservableProperty] private string _corporationName = string.Empty;
    [ObservableProperty] private string _corporationTicker = string.Empty;
    [ObservableProperty] private string _allianceName = string.Empty;
    [ObservableProperty] private string _allianceTicker = string.Empty;
    [ObservableProperty] private string _portraitUrl = string.Empty;

    [ObservableProperty] private bool _isCheckingFleet = true;
    [ObservableProperty] private bool _isInFleet;
    [ObservableProperty] private long _detectedFleetId;
    [ObservableProperty] private string _fleetStatusText = "Checking fleet status...";

    // Subtitle shown under character name: corp [ticker] · alliance or just corp [ticker]
    public string CharacterSubtitle =>
        string.IsNullOrEmpty(AllianceTicker)
            ? (string.IsNullOrEmpty(CorporationName) ? "CAPSULEER" : $"{CorporationName}  [{CorporationTicker}]")
            : $"{CorporationName}  [{CorporationTicker}]  ·  {AllianceName}  [{AllianceTicker}]";

    private async Task LoadCharacterInfoAsync()
    {
        var charInfo = await _esi.GetCharacterPublicInfoAsync(_auth.AuthenticatedCharacterId);
        if (charInfo == null) return;

        var corpInfo = await _esi.GetCorporationPublicInfoAsync(charInfo.CorporationId);
        if (corpInfo != null)
        {
            CorporationName = corpInfo.Name;
            CorporationTicker = corpInfo.Ticker;
        }

        if (charInfo.AllianceId.HasValue && charInfo.AllianceId.Value > 0)
        {
            var alliInfo = await _esi.GetAlliancePublicInfoAsync(charInfo.AllianceId.Value);
            if (alliInfo != null)
            {
                AllianceName = alliInfo.Name;
                AllianceTicker = alliInfo.Ticker;
            }
        }

        OnPropertyChanged(nameof(CharacterSubtitle));
    }

    partial void OnCorporationNameChanged(string value)  => OnPropertyChanged(nameof(CharacterSubtitle));
    partial void OnAllianceNameChanged(string value)     => OnPropertyChanged(nameof(CharacterSubtitle));
    partial void OnAllianceTickerChanged(string value)   => OnPropertyChanged(nameof(CharacterSubtitle));

    private async Task CheckFleetStatusAsync()
    {
        IsCheckingFleet = true;
        var charFleet = await _esi.GetCharacterFleetAsync(_auth.AuthenticatedCharacterId);

        if (charFleet != null)
        {
            IsInFleet = true;
            DetectedFleetId = charFleet.FleetId;
            FleetStatusText = $"Active fleet detected";
        }
        else
        {
            IsInFleet = false;
            FleetStatusText = "No active fleet found.";
        }

        IsCheckingFleet = false;
    }

    [RelayCommand]
    private async Task RefreshFleetStatusAsync() => await CheckFleetStatusAsync();

    [RelayCommand]
    private void OpenSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void OpenDScan() => _shell.ShowIntel();

    [RelayCommand]
    private void OpenSessionLog() => _shell.ShowSessionLog();

    [RelayCommand]
    private void OpenPing() => _shell.ShowPing();

    [RelayCommand(CanExecute = nameof(CanEnterFleet))]
    private void EnterFleet() => _shell.ShowFleet(DetectedFleetId);

    private bool CanEnterFleet() => IsInFleet && DetectedFleetId != 0;

    partial void OnIsInFleetChanged(bool value)       => EnterFleetCommand.NotifyCanExecuteChanged();
    partial void OnDetectedFleetIdChanged(long value) => EnterFleetCommand.NotifyCanExecuteChanged();
}
