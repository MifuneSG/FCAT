namespace FCAT.Models;

/// <summary>
/// Canonical INIT alliance data baked into the INIT ping profile (channel links are stable per
/// channel). Applied to the INIT profile when its lists are empty, so every INIT FC gets them
/// without manual capture. Channel link markup is stored verbatim so re-emitting it always works.
/// </summary>
public static class InitProfileSeed
{
    /// <summary>The Initiative. alliance ID — the INIT profile is locked to members of this alliance.</summary>
    public const int AllianceId = 1900696668;

    public static List<CapturedChannel> BoostLinks() =>
    [
        new() { Label = "I. Boost I",    Markup = "<url=joinChannel:player_c1d16ae185a111ee910700109bd0fa48>I. Boost I</url>" },
        new() { Label = "I. Boost II",   Markup = "<url=joinChannel:player_fee651c085a111eea25200109bd0fa48>I. Boost II</url>" },
        new() { Label = "I. Boost III",  Markup = "<url=joinChannel:player_16c3f04085a211eea19300109bd0fa48>I. Boost III</url>" },
        new() { Label = "I. Boost IV",   Markup = "<url=joinChannel:player_55e3260f85a211ee86bb00109bd0fa48>I. Boost IV</url>" },
        new() { Label = "I. Boost V",    Markup = "<url=joinChannel:player_707ed31e85a211ee9a4700109bd0fa48>I. Boost V</url>" },
        new() { Label = "I. Boost VI",   Markup = "<url=joinChannel:player_5a882dee866f11eeb16000109bd0fa48>I. Boost VI</url>" },
        new() { Label = "I. Boost VII2", Markup = "<url=joinChannel:player_136e7b0fb7d511f098f73a68dd86f9e7>I. Boost VII2</url>" },
        new() { Label = "I. Boost VIII", Markup = "<url=joinChannel:player_19405951b7d511f0ac073a68dd86f9e7>I. Boost VIII</url>" },
        new() { Label = "I. Boost IX",   Markup = "<url=joinChannel:player_2b495a70b7d511f0b3393a68dd86f9e7>I. Boost IX</url>" },
        new() { Label = "I. Boost X2",   Markup = "<url=joinChannel:player_396ae82eb7d511f09f273a68dd86f9e7>I. Boost X2</url>" },
        new() { Label = "I. Boost XI",   Markup = "<url=joinChannel:player_3d54f3a1b7d511f0b8fd3a68dd86f9e7>I. Boost XI</url>" },
        new() { Label = "I. Boost XII",  Markup = "<url=joinChannel:player_3f6f6f30b7d511f0b6723a68dd86f9e7>I. Boost XII</url>" },
        new() { Label = "I. Boost XIII", Markup = "<url=joinChannel:player_4104670fb7d511f0abc93a68dd86f9e7>I. Boost XIII</url>" },
        new() { Label = "I. Boost XIV",  Markup = "<url=joinChannel:player_45083c0fb7d511f0a39a3a68dd86f9e7>I. Boost XIV</url>" },
        new() { Label = "I. Boost XV",   Markup = "<url=joinChannel:player_478c4edeb7d511f081d83a68dd86f9e7>I. Boost XV</url>" },
        new() { Label = "I. Boost FD",   Markup = "<url=joinChannel:player_4d50873086c011ee882300109bd0fa48>I. Boost FD</url>" },
    ];

    public static List<CapturedChannel> LogiLinks() =>
    [
        new() { Label = "I. Logistics I",    Markup = "<url=joinChannel:player_3cfd230f8c5711eea43300109bd0f828>I. Logistics I</url>" },
        new() { Label = "I. Logistics II",   Markup = "<url=joinChannel:player_3f53480f8c5711eea83e00109bd0f828>I. Logistics II</url>" },
        new() { Label = "I. Logistics III",  Markup = "<url=joinChannel:player_41e787808c5711ee909d00109bd0f828>I. Logistics III</url>" },
        new() { Label = "I. Logistics IV",   Markup = "<url=joinChannel:player_43a0a9308c5711eea25f00109bd0f828>I. Logistics IV</url>" },
        new() { Label = "I. Logistics V",    Markup = "<url=joinChannel:player_456a1e918c5711ee985300109bd0f828>I. Logistics V</url>" },
        new() { Label = "I. Logistics VII",  Markup = "<url=joinChannel:player_744f1f51b7c811f0b9a600109bd0fca8>I. Logistics VII</url>" },
        new() { Label = "I. Logistics VIII", Markup = "<url=joinChannel:player_bdd640cfb7c911f0a17300109bd0fca8>I. Logistics VIII</url>" },
        new() { Label = "I. Logistics IX",   Markup = "<url=joinChannel:player_a3f14421b7ca11f0b64e00109bd0fca8>I. Logistics IX</url>" },
        new() { Label = "I. Logistics X",    Markup = "<url=joinChannel:player_57c8b1e1b7cb11f0a3643a68dd86f9e7>I. Logistics X</url>" },
        new() { Label = "I. Logistics XI2",  Markup = "<url=joinChannel:player_922efe0fb7cc11f0bda73a68dd86f9e7>I. Logistics XI2</url>" },
        new() { Label = "I. Logistics XII",  Markup = "<url=joinChannel:player_21bd3780b7ce11f0b54e3a68dd86f9e7>I. Logistics XII</url>" },
        new() { Label = "I. Logistics XIII", Markup = "<url=joinChannel:player_ae73190fb7ce11f09bea3a68dd86f9e7>I. Logistics XIII</url>" },
        new() { Label = "I. Logistics XIV",  Markup = "<url=joinChannel:player_593e1700b7cf11f09d6f3a68dd86f9e7>I. Logistics XIV</url>" },
        new() { Label = "I. Logistics FD",   Markup = "<url=joinChannel:player_4ecd39de8c5711ee8a5e00109bd0f828>I. Logistics FD</url>" },
    ];

    // INIT Mumble comms channels (plain text in the MOTD — Mumble isn't an in-game channel link).
    public static List<string> CommsChannels() =>
    [
        "Actually Inactive Main",
        "Astartes Main",
        "Reinforcements",
        "Silk Road",
        "Special Ops",
        "Black Templars Main",
        "Fleet Sec Blue Main",
        "Fleet Sec Green Main",
        "Fleet Sec Purple I Main",
        "Fleet Sec Purple II Main",
        "Fleet Sec Purple III Main",
        "Fleet Sec Purple IV Dark Shines Main",
        "Fleet Sec Purple V Main",
        "Fleet Sec Purple VI Main",
        "Fleet Sec VII Main",
        "Fleet Sec VIII Main",
        "Fleet Sec IX Main",
        "Fleet Sec X Main",
        "Fleet Sec Purple XI Main",
        "Fleet Sec Purple XII Main",
        "Fleet Sec Purple XIII Main",
        "Fleet Sec Purple XIV Main",
        "Fleet Sec Purple XV Main",
        "Fleets Roaming Main I",
        "Fleets Roaming Main II",
        "Fleet Roaming Main III",
    ];

    public static List<DoctrinePreset> Doctrines() =>
    [
        // Cheesy — Omen Navy Issue skirmish cruiser gang (Vagabond FC, Scalpel logi, Stork/Bifrost
        // boosts, Hyena LR paint/web, i.Hyperspatial Sabre/Flycatcher dictors as general fits).
        new() { Category = "Skirmish", Name = "Cheesy - Omen Navy Issue",    Ships = "Logi (Scalpel) > Boosts (Stork/Bifrost) > DPS (Omen Navy Issue) > Else", FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/187/" },
        new() { Category = "Skirmish", Name = "Daisy Cutter - Bombers",      Ships = "Purifier > Hound",                                                      FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/23/" },
        new() { Category = "Skirmish", Name = "Haunter - Svipul",            Ships = "Logi (Kirin) > Boosts (Stork/Bifrost) > DPS (Svipul) > Else",            FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/162/" },
        new() { Category = "Skirmish", Name = "Kikistuka - Kikimora",        Ships = "Logi (Kirin) > Boosts (Stork/Bifrost) > DPS (Kikimora) > Else",          FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/20/" },
        new() { Category = "Skirmish", Name = "Ok Beamer - Maller",          Ships = "Logi (Augoror) > Boosts (Prophecy) > DPS (Maller) > Else",               FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/120/" },
        new() { Category = "Skirmish", Name = "Meat Grinders - Hurricane",   Ships = "Logi (Osprey/Scythe) > Boosts (Stork/Bifrost) > DPS (Hurricane) > Else", FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/175/" },
        new() { Category = "Skirmish", Name = "Skol - Munnin",               Ships = "Logi (Scimitar/Scythe) > Boosts (Claymore) > DPS (Munnin) > Else",       FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/14/" },
        new() { Category = "Skirmish", Name = "Sm0l Beamers - Retributions", Ships = "Logi (Deacon) > Boosts (Pontifex/Magus) > DPS (Retris) > Else",          FittingUrl = "https://zero.the-initiative.rocks/fittings/doctrine/75/" },
    ];
}
