namespace FCAT.Models;

/// <summary>One authorized EVE character FCAT knows about. The refresh token is sensitive —
/// the whole list is DPAPI-encrypted on disk (see CharacterStore).</summary>
public class StoredCharacter
{
    public int    CharacterId   { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string RefreshToken  { get; set; } = string.Empty;

    /// <summary>FC-assigned label: Main / Cyno / Scout / Hauler / Titan / … (free text).</summary>
    public string Role { get; set; } = "Main";

    /// <summary>The character FCAT currently operates as (fleet ops, dashboard identity).</summary>
    public bool IsActive { get; set; }
}
