namespace FCAT.ViewModels;

/// <summary>A destination role/position a pilot can be moved to, shown in the move picker.</summary>
public class MoveTargetViewModel(string label, string role, long? wingId, long? squadId)
{
    public string Label   { get; } = label;
    public string Role    { get; } = role;
    public long?  WingId  { get; } = wingId;
    public long?  SquadId { get; } = squadId;
}
