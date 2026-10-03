using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FCAT.Models;

namespace FCAT.ViewModels;

public partial class WingViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    public long WingId { get; }
    public ObservableCollection<FleetMemberViewModel> WingCommanders { get; } = [];
    public ObservableCollection<SquadViewModel> Squads { get; } = [];

    public WingViewModel(FleetWing wing)
    {
        WingId = wing.Id;
        Name = wing.Name;

        // By id for the same reason the wings are - see RebuildHierarchy in FleetViewModel.
        foreach (var squad in wing.Squads.OrderBy(s => s.Id))
            Squads.Add(new SquadViewModel(squad));
    }

    public WingViewModel(long wingId, string name)
    {
        WingId = wingId;
        Name = name;
    }
}
