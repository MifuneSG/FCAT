# Boost Tracking — BDD Specification (EARS)

**Component:** `BoostChannelService` (`FCAT/Services/BoostChannelService.cs`)
**Component:** `BoostChargeCatalog` (`FCAT/Models/BoostCharge.cs`)

## Overview

Tracks which command-burst charges each booster is running by tailing the fleet's boost channel
in EVE Chatlogs. Boosters "drag" their charge items into the channel; the service matches the
item names against a canonical catalogue.

## Requirements

### Charge catalogue

The system shall maintain a catalogue of all 15 command-burst charges across 7 boost categories
(Skirmish, Armor, Shield, Info, Mining Yield, Mining Optimal, Mining Preserve).

**When** `FindIn` is called with text containing a charge name, the system shall return all matching
charges, using case-insensitive substring matching.

**When** `FindIn` is called with text that contains no charge names, the system shall return an empty
collection.

### Category tags

**When** `CategoryTag` is called with a `BoostCategory`, the system shall return the correct
abbreviated tag (SKIRM, ARMOR, SHIELD, INFO, YIELD, RANGE, PRESV).

### Chat log parsing

**When** the service reads a chatlog line matching `[ timestamp ] Speaker > message`, the system
shall extract the speaker name and message text.

**If** the speaker is `EVE System`, then the system shall ignore the line (it is the channel MOTD
listing all charges).

**When** the message contains one or more charge names, the system shall record the full set as
that pilot's current loadout. The last message wins — a new post replaces the previous loadout.

**When** the message contains no recognised charge names, the system shall ignore it.

### Loadout lifecycle

**When** `GetLoadout` is called for a pilot who has posted charges, the system shall return their
current loadout.

**When** `ClearPilot` is called, the system shall remove that pilot's loadout and raise `Updated`.

### File tracking

**When** `StartWatching` is called, the system shall find the most recent chatlog file matching the
channel prefix and read it from the beginning (to capture loadouts posted before launch).

**When** `Refresh` is called and a newer session file exists, the system shall switch to it and
re-read from the beginning.

## Test Coverage

- `BoostChargeCatalogTests.All_Contains15Charges`
- `BoostChargeCatalogTests.FindIn_MatchesSingleCharge`
- `BoostChargeCatalogTests.FindIn_MatchesMultipleCharges`
- `BoostChargeCatalogTests.FindIn_CaseInsensitive`
- `BoostChargeCatalogTests.FindIn_NoMatch_ReturnsEmpty`
- `BoostChargeCatalogTests.CategoryTag_ReturnsCorrectTag`
