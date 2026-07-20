# FCAT Architecture

## System Overview

FCAT is a Windows desktop WPF application built on .NET 10 using the MVVM pattern via
CommunityToolkit.Mvvm. It is a companion tool for EVE Online fleet commanders, providing real-time
fleet monitoring, combat alerts, intel feeds, and fleet management through the EVE ESI API.

```
┌─────────────────────────────────────────────────────────────┐
│                    WPF Application Shell                     │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐    │
│  │  Views    │  │  Views   │  │  Views   │  │  Views   │    │
│  │  Login    │  │  Fleet   │  │  Intel   │  │  Settings │   │
│  │  Menu     │  │  Alerts  │  │  Ping    │  │  Session  │   │
│  │  Account  │  │          │  │          │  │  Log      │   │
│  └─────┬─────┘  └─────┬────┘  └────┬─────┘  └─────┬─────┘  │
│        │              │            │              │          │
│  ┌─────┴──────────────┴────────────┴──────────────┴──────┐  │
│  │                    ViewModels (MVVM)                    │  │
│  │  ShellVM → LoginVM, MenuVM, FleetVM, IntelVM, PingVM   │  │
│  │           AlertsVM, SettingsVM, SessionLogVM, AccountVM │  │
│  └─────────────────────────┬──────────────────────────────┘  │
│                            │                                 │
│  ┌─────────────────────────┴──────────────────────────────┐  │
│  │                      Services                          │  │
│  │  EsiAuthService ─ EsiService ─ AlertHub ─ SessionLog   │  │
│  │  CombatLogService ─ BoostChannelService ─ IntelChannel │  │
│  │  SettingsService ─ CharacterStore ─ ThemeService        │  │
│  │  SoundService ─ SystemSearchService ─ ZkillService     │  │
│  │  BattleReportService ─ UpdaterService ─ WormholeMass   │  │
│  └────────────────────────────────────────────────────────┘  │
│                            │                                 │
│  ┌─────────────────────────┴──────────────────────────────┐  │
│  │                       Models                           │  │
│  │  ShipRole ─ BoostCharge ─ FleetModels ─ AppSettings    │  │
│  │  FcAlert ─ IntelEntry ─ PingProfile ─ EsiToken         │  │
│  └────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
    EVE SSO OAuth2       EVE ESI REST API     EVE Local Files
    (port 7648)          (esi.evetech.net)    (Gamelogs, Chatlogs)
                              │
                         zKillboard API
```

## Layer Descriptions

### Models (`FCAT/Models/`)

Plain data classes, enums, and ESI DTOs. No logic beyond computed display properties. Key files:

- **ShipRole.cs** — `ShipRole` enum (14 roles) + `ShipRoleClassifier` static class that maps ESI
  group/type IDs to fleet roles. Contains the group map, type overrides, and capsule/cap-chain
  hull constants.
- **BoostCharge.cs** — `BoostCategory` enum + `BoostChargeCatalog` with all 15 command-burst charges
  and substring matching for chat log parsing.
- **FleetModels.cs** — All ESI DTOs: fleet info, members, wings/squads, character info, universe
  data, killmails, zKillboard models, server status.
- **BoostAlert.cs** — `AlertType` enum + `FcAlert` model with computed display properties.
- **AppSettings.cs** — User-configurable settings persisted to `%APPDATA%\FCAT\settings.json`.
- **PingProfile.cs** — Ping/MOTD profiles with doctrine presets and captured channel links.

### Services (`FCAT/Services/`)

Business logic, external integrations, and file I/O. All services are plain classes without
interfaces, manually wired in `App.OnStartup()`.

| Service | Responsibility |
|---------|---------------|
| `EsiAuthService` | EVE SSO OAuth2 with PKCE, multi-character token management |
| `EsiService` | ESI REST API client (40+ endpoints) |
| `CombatLogService` | Tails EVE gamelog, raises tackle/cap-out alerts |
| `BoostChannelService` | Tails boost chat channel, tracks charge loadouts |
| `IntelChannelService` | Tails intel chat channel, region-filtered reports |
| `AlertHub` | App-lifetime alert feed, sound playback, overlay state |
| `SessionLog` | After-action report timeline + battle report anchors |
| `CharacterStore` | DPAPI-encrypted character persistence |
| `SettingsService` | JSON settings load/save |
| `SoundService` | Procedural WAV synthesis + throttled playback |
| `SystemSearchService` | Offline system name autocomplete |
| `ThemeService` | Live colour theming (4 themes) |
| `ZkillService` | zKillboard API client |
| `BattleReportService` | Battle reports from zKillboard grouped fights |
| `WormholeMass` | Fleet mass vs wormhole capacity checks |
| `MotdParser` | Extracts channel links from fleet MOTDs |
| `UpdaterService` | Velopack auto-update from GitHub Releases |

### ViewModels (`FCAT/ViewModels/`)

MVVM ViewModels using CommunityToolkit.Mvvm source generators (`[ObservableProperty]`,
`[RelayCommand]`). The `ShellViewModel` is the root and owns navigation + the fleet session.

The fleet session (`FleetViewModel`) survives page navigation — it's kept alive on `ShellViewModel._session`
so alerts, boost tracking, and the overlay continue while browsing other pages.

### Views (`FCAT/Views/`)

9 WPF UserControls (`.xaml` + `.xaml.cs` pairs). Pure presentation — all logic is in ViewModels.
ViewModel-to-View mapping is done via `DataTemplate` declarations in `App.xaml`.

## Dependency Graph

```
Program.cs
  → VelopackApp.Build().Run()
  → App.OnStartup()

App.OnStartup() creates (in order):
  HttpClient (15s timeout)
  CharacterStore
  EsiAuthService(HttpClient, CharacterStore)
  EsiService(HttpClient, EsiAuthService)
  CombatLogService()
  SettingsService()
  ThemeService.Apply(...)
  SessionLog()
  AlertHub(SettingsService, SessionLog)
  SystemSearchService(EsiService)
  ZkillService(HttpClient)
  BattleReportService(ZkillService, EsiService)
  UpdaterService()
  ShellViewModel(all services)
  MainWindow(AlertHub)
```

There is no dependency injection container. All services are manually constructed and passed as
constructor arguments. This keeps the dependency graph explicit and easy to trace.

## Data Flows

### Fleet Monitoring (5-second poll loop)

```
FleetViewModel.PollFleetAsync()
  → EsiService.GetFleetMembersAsync()           // ESI REST call
  → EsiService.ResolveNamesAsync()              // batch name resolution
  → EsiService.GetShipGroupIdsAsync()           // ship type info
  → ShipRoleClassifier.Classify(typeId, groupId) // role assignment
  → Build WingVM/SquadVM/FleetMemberVM hierarchy
  → Compare previous roster (detect deaths, joins, leaves)
  → BoostChannelService.Refresh()               // check for new boost posts
  → Attach boost loadouts to fleet member rows
  → Compute fleet composition stats + advisories
```

### Alert Pipeline

```
EVE Gamelog → CombatLogService (FileSystemWatcher + StreamReader)
  → Regex match (tackle / cap-out)
  → AlertRaised event
  → FleetViewModel handler
  → AlertHub.Raise(alert)
    → Insert into Alerts + OverlayAlerts
    → SoundService.PlayThrottled()
    → SessionLog.Record()
    → Schedule ExpireAlertAsync() for overlay timeout

Fleet death detection (FleetViewModel):
  → Ship-to-Capsule transition across polls
  → BoostLost / LogiChain / DpsLoss alert
  → AlertHub.Raise()
```

### Boost Tracking

```
EVE Chatlogs → BoostChannelService (FileSystemWatcher + polling)
  → ChatLineRegex match → extract speaker + message
  → BoostChargeCatalog.FindIn(message) → match charge names
  → Store loadout per speaker
  → Updated event → FleetViewModel attaches loadouts to member rows
```

### Intel Feed

```
ZkillService (polling) → kills in region systems
IntelChannelService (chatlog tailing) → intel reports
  → IntelFeedViewModel merges both streams
  → SystemSearchService detects system names
  → Display in unified feed with kill/intel tags
```

## Key Design Decisions

1. **No DI container**: All services are manually wired in `App.OnStartup()`. For a single-project
   desktop app this keeps things simple and the dependency graph fully explicit.

2. **App-lifetime services**: `AlertHub`, `SessionLog`, and the fleet session survive page navigation.
   This means the alert overlay stays up and events keep recording regardless of which page the FC
   is viewing.

3. **Demo mode**: `EsiService.DemoMode` flag makes fleet-related calls return synthetic data from
   `DemoData`. This lets all features be exercised without an EVE account.

4. **No interfaces**: Services are concrete classes. This is appropriate for a single-project app
   where the service implementations won't be swapped.

5. **DPAPI encryption**: Character tokens are encrypted at rest using Windows DPAPI, so credentials
   are bound to the user's Windows account.

6. **Procedural audio**: Alert sounds are synthesized to WAV from frequency/duration segments rather
   than bundled as audio files. This keeps the deployment self-contained.

## File Organisation

```
FCAT/
├── Models/          10 files — data classes, enums, DTOs
├── Services/        17 files — business logic, integrations, I/O
├── ViewModels/      18 files — MVVM presentation logic
├── Views/            9 files — WPF XAML + code-behind (pairs)
├── App.xaml          Design system: palette, typography, button styles, DataTemplates
├── Converters.cs     12 WPF value converters
├── MainWindow.xaml   Shell chrome: nav rail, top bar, content area
├── AlertOverlayWindow.xaml  Floating overlay for combat alerts
├── Program.cs        Entry point with Velopack hooks
└── FCAT.csproj       Project file (.NET 10, WPF, Velopack)
```
