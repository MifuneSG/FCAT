# Combat Alerts — BDD Specification (EARS)

**Component:** `CombatLogService` (`FCAT/Services/CombatLogService.cs`)

## Overview

Tails the FC's EVE gamelog directory and raises real-time alerts for combat events that EVE
writes to disk. Only two event types produce reliable gamelog entries: warp scramble/disruption
(tackle) and module deactivation from cap depletion.

## Constraints

EVE's gamelog does **not** log most electronic warfare. Webs, neuts, ECM, tracking disruptors,
sensor dampeners, and target painters produce no gamelog line. This service intentionally handles
only what can be detected.

## Requirements

### File watching

**When** `StartWatching` is called with a valid gamelog directory, the system shall attach a
`FileSystemWatcher` and begin monitoring for new log lines.

**When** `StartWatching` is called with a non-existent directory, the system shall return without
error and not start watching.

**When** a new `.txt` file is created in the watched directory, the system shall switch to that
file as the active log.

### Log line parsing

**When** a new line matching `[ YYYY.MM.DD HH:MM:SS ] (category) content` is written to the active
log file, the system shall parse the timestamp, category, and content.

**If** the category is not `combat` or `notify`, then the system shall ignore the line.

### Tackle detection

**When** the content matches `Warp scramble attempt from <attacker> to you`, the system shall raise
a `Tackled` alert with the attacker's name.

**If** the attacker name is `you` (the FC tackling someone else), then the system shall **not**
raise an alert — the FC does not need to be told they are tackling.

### Cap-out detection

**When** the content matches a module deactivating due to capacitor (e.g. `<Module> deactivates as
the capacitor runs out of charge`), the system shall raise a `CapTrouble` alert with the module name.

### Lifecycle

**When** `StopWatching` is called, the system shall dispose the `FileSystemWatcher` and stop
reading new lines.

The system shall be safely disposable and shall not throw when `Dispose` is called multiple times.

## Test Coverage

- `CombatLogParsingTests.StartWatching_NonExistentDirectory_DoesNotThrow`
- `CombatLogParsingTests.StopWatching_WhenNotStarted_DoesNotThrow`
- `CombatLogParsingTests.Dispose_CalledMultipleTimes_DoesNotThrow`
- `CombatLogParsingTests.ParseLine_Tackle_RaisesAlert`
- `CombatLogParsingTests.SelfTackle_ShouldNotTriggerAlert`
- `CombatLogParsingTests.CapOut_LogLineFormat_MatchesExpectedPattern`
