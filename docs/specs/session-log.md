# Session Log (After-Action Report) — BDD Specification (EARS)

**Component:** `SessionLog` (`FCAT/Services/SessionLog.cs`)

## Overview

App-lifetime after-action log for the current fleet operation. Alerts and fleet events (joins,
leaves, deaths) are recorded here so the FC can review or export the timeline afterwards. Lives
for the whole app session and survives page navigation.

## Requirements

### Recording

**When** `Record` is called, the system shall insert a new `AarEntry` at the top of the `Entries`
collection (newest-first for the on-screen panel).

**When** the entry count exceeds 2000, the system shall trim the oldest entries to stay within the cap.

### Session lifecycle

**When** `StartSession` is called, the system shall:
1. Set `SessionStart` if not already set (preserves the original start across fleet re-forms)
2. Record a SESSION entry with the fleet ID and FC name
3. Update the summary text

**When** `Clear` is called, the system shall reset all state: entries, session start, combat anchors,
battle sides, and summary.

### Battle report anchors

**When** `MarkCombat` is called with a valid system ID, the system shall add a `CombatAnchor`
recording where and when the fleet fought.

**If** a system already has an anchor within the hourly window, then the system shall not add a
duplicate.

**If** the system ID is 0 or negative, then the system shall ignore the call.

### Battle sides

**When** `SetBattleSides` is called for the first time, the system shall record the friendly
character IDs and alliance ID.

**If** `SetBattleSides` is called again, then the system shall ignore it — the opening roster
is frozen.

### Display properties

**When** the fleet has fought in one system, `BattleReportSystem` shall return the system name.

**When** the fleet has fought in multiple systems, `BattleReportSystem` shall return `"N systems"`.

**When** there are no combat anchors, `BattleReportSystem` shall return an empty string, and
`BattleReportZkill` / `BattleReportEvetools` shall return null.

### Export

**When** `Export` is called, the system shall produce a Markdown document with:
- Header with fleet ID, FC name, start time, and event count
- Battle Report section (if anchors exist) with zKillboard links
- Timeline section with all events in chronological order (oldest first)

**When** `ExportForCopy` is called, the system shall produce a plain-text version suitable for
pasting into Discord (no Markdown tables or headings that render as raw characters).

## Test Coverage

- `SessionLogTests.Record_AddsEntry`
- `SessionLogTests.Record_InsertsAtTop_NewestFirst`
- `SessionLogTests.Record_CapsAt2000`
- `SessionLogTests.Clear_RemovesAllEntries`
- `SessionLogTests.StartSession_SetsSessionStart`
- `SessionLogTests.StartSession_PreservesOriginalStart`
- `SessionLogTests.MarkCombat_AddsAnchor`
- `SessionLogTests.MarkCombat_InvalidSystemId_Ignored`
- `SessionLogTests.BattleReportSystem_SingleSystem_ReturnsName`
- `SessionLogTests.BattleReportSystem_MultipleSystems_ReturnsCount`
- `SessionLogTests.SetBattleSides_FreezesOnFirstCall`
- `SessionLogTests.Export_ProducesMarkdownWithHeader`
- `SessionLogTests.ExportForCopy_ProducesPlainText`
