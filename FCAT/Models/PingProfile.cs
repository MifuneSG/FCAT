namespace FCAT.Models;

/// <summary>
/// A captured in-game chat-channel link. We store the raw MOTD markup verbatim
/// (e.g. <c>&lt;url=joinChannel:...&gt;I. Boost IV&lt;/url&gt;</c>) so re-emitting it always
/// produces a working clickable link — the channel's internal id is baked into the markup and
/// can't be derived from the name via ESI.
/// </summary>
public class CapturedChannel
{
    public string Label  { get; set; } = string.Empty;   // e.g. "I. Boost IV" (the link text)
    public string Markup { get; set; } = string.Empty;   // the full <url=joinChannel:...>...</url>
}

/// <summary>
/// A doctrine preset: the doctrine name, its ship-priority line, and the auth fitting-page URL.
/// Selecting a doctrine fills the Ships line and links the doctrine to its fitting page in the MOTD.
/// </summary>
public class DoctrinePreset
{
    public string Name       { get; set; } = string.Empty;
    public string Ships      { get; set; } = string.Empty;   // priority order, e.g. "Napoc > Guardian > Boosts > HICs"
    public string FittingUrl { get; set; } = string.Empty;   // auth fitting-area link
    public string Category   { get; set; } = string.Empty;   // e.g. "Skirmish" — for future grouping
}

/// <summary>
/// A ping/MOTD profile. Typically one per alliance (INIT) plus a personal/Custom one for people
/// who don't use alliance-level channels. Holds the editable autofill lists and the captured
/// clickable channel links.
/// </summary>
public class PingProfile
{
    public string Name     { get; set; } = string.Empty;
    public bool   Alliance { get; set; }

    /// <summary>If non-zero, only characters in this alliance may select the profile. 0 = open to anyone.</summary>
    public int    AllianceId { get; set; }

    // Editable autofill lists (managed in-app, grow as you use them).
    public List<DoctrinePreset> Doctrines     { get; set; } = [];
    public List<string>         CommsChannels { get; set; } = [];

    // Capture-first clickable links, captured from a live fleet MOTD via ESI.
    public List<CapturedChannel> BoostLinks { get; set; } = [];
    public List<CapturedChannel> LogiLinks  { get; set; } = [];
}
