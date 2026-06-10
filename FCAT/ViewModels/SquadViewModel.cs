using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FCAT.Models;

namespace FCAT.ViewModels;

public partial class SquadViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    public long SquadId { get; }
    public ObservableCollection<FleetMemberViewModel> Members { get; } = [];

    public SquadViewModel(FleetSquad squad)
    {
        SquadId = squad.Id;
        Name = squad.Name == "Squad 1" || squad.Name.StartsWith("Squad")
            ? squad.Name
            : squad.Name;
    }

    public SquadViewModel(long squadId, string name)
    {
        SquadId = squadId;
        Name = name;
    }
}
