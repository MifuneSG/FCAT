# Wormhole Mass — BDD Specification (EARS)

**Component:** `WormholeMass` (`FCAT/Services/WormholeMass.cs`)

## Overview

Checks whether the fleet's total hull mass can fit through standard wormhole classes. Hole totals
come from the EVE University wormhole table and vary +/-10% at spawn, so checks run against 90%
of nominal (worst-case spawn). Fleet mass is base hull only — ESI doesn't expose other pilots'
fits.

## Requirements

### Mass formatting

**When** `Format` is called with a mass value, the system shall display it in human-readable form:
- >= 1B kg: `X.XXB kg`
- >= 1M kg: `X.XM kg`
- < 1M kg: `N kg` with thousands separators

### Hole checks

The system shall check fleet mass against 4 wormhole classes: 1B, 2B, 3B, and 5B.

**When** `Checks` is called with a fleet mass, the system shall return one `WormholeCheck` per class
containing:
- `OneWay`: whether the fleet fits in a single pass (mass <= 90% of hole total)
- `RoundTrip`: whether the fleet fits a round trip (mass * 2 <= 90% of hole total)

### Small fleet

**When** a fleet's total mass is small relative to all hole classes, the system shall report all
holes as passable for both one-way and round-trip.

### Large fleet

**When** a fleet's total mass exceeds the 90% budget of a hole class, the system shall report that
class as not passable one-way (and therefore not round-trip either).

### Round-trip logic

**When** a fleet fits one-way but its doubled mass exceeds the budget, the system shall report
one-way as passable but round-trip as not passable.

### Text properties

The system shall provide `OneWayText` and `RoundTripText` properties that return `"yes"` or `"no"`
matching their boolean counterparts.

## Test Coverage

- `WormholeMassTests.Format_DisplaysCorrectUnit`
- `WormholeMassTests.Checks_Returns4Entries`
- `WormholeMassTests.Checks_SmallFleet_FitsAllHoles`
- `WormholeMassTests.Checks_LargeFleet_ExceedsSomeHoles`
- `WormholeMassTests.Checks_RoundTrip_RequiresDoubleMassFit`
- `WormholeMassTests.Checks_TextProperties_MatchBooleans`
- `WormholeMassTests.Checks_HoleLabels_AreCorrect`
