# Ship Classification — BDD Specification (EARS)

**Component:** `ShipRoleClassifier` (`FCAT/Models/ShipRole.cs`)

## Overview

Classifies every fleet member's ship into a fleet role (Logi, DPS, Tackle, etc.) based on the
ESI `group_id` and `type_id` of their hull. This drives the fleet composition display, role badges,
and downstream alerts (booster death, logi chain break, DPS attrition).

## Requirements

### Group-based classification

**When** a ship's `group_id` matches a known fleet-role group, the system shall classify it into the
corresponding `ShipRole`.

| group_id | Role |
|----------|------|
| 832, 1527 | Logi |
| 1538 | CapLogi |
| 540, 1534 | Booster |
| 30 | Titan |
| 659 | Supercarrier |
| 547, 485 | CapDPS |
| 883, 941, 28, 380 | Industrial |
| 463, 543, 1283 | Mining |
| 831, 894 | Tackle |
| 541 | Bubble |
| 906, 833, 893 | EWAR |
| 830, 1022 | Support |

### Type-override precedence

**When** a ship's `type_id` has a per-type override defined, the system shall use the override role
**instead of** the group-based role, even if the group is also mapped.

Examples:
- Venture (32880) in Frigate group (25) shall classify as Mining, not DPS
- Osprey (620) in Cruiser group (26) shall classify as Logi, not DPS
- Nestor (33472) in Battleship group (27) shall classify as Logi, not DPS

### Fallback to DPS

**When** a ship's `type_id` has no override **and** its `group_id` is not in the group map, the system
shall classify it as `DPS`.

### Capsule detection

**When** a pilot's `group_id` equals 29 (Capsule), the system shall identify them as podded.

**If** a pilot transitions from a ship group to the Capsule group between fleet polls, then the system
shall treat this as a ship loss (handled by `FleetViewModel`).

### Cap-chain logi identification

The system shall maintain a set of hull type IDs for cap-chain logistics ships (Guardian, Basilisk,
Osprey, Augoror).

**When** a pilot in a cap-chain hull is lost, the system shall raise a `LogiChain` alert indicating
the cap ring needs re-forming.

**If** a pilot is flying a cap-independent logi ship (Scimitar, Oneiros), then the system shall
**not** include them in the cap-chain set.

## Test Coverage

- `ShipRoleClassifierTests.Classify_GroupMapped_ReturnsCorrectRole`
- `ShipRoleClassifierTests.Classify_TypeOverride_TakesPrecedenceOverGroup`
- `ShipRoleClassifierTests.Classify_UnknownGroupAndType_FallsToDPS`
- `ShipRoleClassifierTests.IsCapsule_IdentifiesCapsuleGroup`
- `ShipRoleClassifierTests.CapChainHullTypeIds_ContainsOnlyCapChainLogi`
