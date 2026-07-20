# Alert System — BDD Specification (EARS)

**Component:** `AlertHub` (`FCAT/Services/AlertHub.cs`)
**Component:** `FcAlert` (`FCAT/Models/BoostAlert.cs`)

## Overview

App-lifetime alert feed that receives combat alerts and fleet events, plays sounds, manages the
on-screen overlay, and records all alerts to the after-action report. Lives for the entire app
session and survives page navigation.

## Requirements

### Alert model

**When** an `FcAlert` is created, the system shall provide:
- `AlertTag`: short label per type (TACKLED, CAP OUT, BOOST LOST, LOGI CHAIN, DPS LOSS, INFO)
- `Headline`: human-readable description per type
- `SubText`: attacker name for tackle, detail string for all others
- `IsCritical`: true by default for Tackled alerts, false for others

**Where** `CriticalOverride` is set, the system shall use it instead of the type-based default.

### Alert raising

**When** `Raise` is called with an alert, the system shall:
1. Insert it at the top of both the persistent `Alerts` feed and the `OverlayAlerts` feed
2. Cap both collections at 100 entries
3. Increment the `UnreadCount`
4. Record the alert to the `SessionLog`
5. Play the configured sound (if sounds are enabled)

### Sound mapping

**While** alert sounds are enabled, the system shall play the user's configured preset per type:
- Tackled -> `TackledSound` setting
- CapTrouble -> `CapTroubleSound` setting
- BoostLost -> `BoostLostSound` setting
- LogiChain -> same as BoostLost
- DpsLoss -> same as Tackled

**When** the same alert type fires multiple times within 2 seconds, the system shall throttle
playback to prevent "machine-gunning."

### Overlay expiry

**When** `AlertClearSeconds` is greater than 0, the system shall remove alerts from `OverlayAlerts`
after the configured timeout.

**If** `AlertClearSeconds` is 0, then alerts shall remain in the overlay until manually cleared.

The persistent `Alerts` feed shall **never** auto-clear — it keeps the full session history.

### Unread tracking

**When** `MarkRead` is called, the system shall reset `UnreadCount` to 0.

### Overlay state persistence

**When** the overlay position or lock state changes, the system shall persist it via `SettingsService`
so it survives app restarts.

## Test Coverage

- `FcAlertTests.AlertTag_ReturnsCorrectTag`
- `FcAlertTests.Headline_ReturnsCorrectText`
- `FcAlertTests.SubText_Tackled_ReturnsAttackerName`
- `FcAlertTests.SubText_Tackled_NoAttacker_ReturnsUnknown`
- `FcAlertTests.IsCritical_Tackled_TrueByDefault`
- `FcAlertTests.IsCritical_CriticalOverride_True_OverridesDefault`
