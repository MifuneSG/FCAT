# CLAUDE.md — FCAT Development Context

## What is this project?

FCAT (Fleet Commander Assistance Tool) is a Windows desktop companion app for EVE Online fleet
commanders. It monitors fleet composition, raises combat alerts, tracks booster loadouts, provides
intel feeds, and manages fleet operations through the EVE ESI API.

## Tech stack

- **Language:** C# on .NET 10
- **UI:** WPF (Windows Presentation Foundation)
- **Architecture:** MVVM via CommunityToolkit.Mvvm (source generators)
- **No DI container** — services are manually wired in `App.OnStartup()`
- **No interfaces** — services are concrete classes
- **Testing:** xUnit + FluentAssertions in `FCAT.Tests/`

## Build and test

```
dotnet build                    # build the solution
dotnet test                     # run all tests
dotnet run --project FCAT/FCAT.csproj   # run the app
```

## Project layout

- `FCAT/Models/` — Data classes, enums, ESI DTOs (no logic beyond display properties)
- `FCAT/Services/` — Business logic, API clients, file I/O
- `FCAT/ViewModels/` — MVVM ViewModels with `[ObservableProperty]` / `[RelayCommand]`
- `FCAT/Views/` — WPF XAML UserControls (pure presentation)
- `FCAT/App.xaml` — Design system + service wiring
- `FCAT.Tests/` — Unit tests for pure-logic components
- `docs/specs/` — BDD requirements in EARS syntax
- `docs/ARCHITECTURE.md` — Full architecture overview

## Key conventions

- File-scoped namespaces, primary constructors, collection expressions
- No comments unless the WHY is non-obvious
- Services have no interfaces — they're concrete classes passed via constructors
- The fleet session survives page navigation (kept on `ShellViewModel._session`)
- `AlertHub` and `SessionLog` live for the entire app lifetime
- Demo mode: `EsiService.DemoMode` returns synthetic data from `DemoData`

## What NOT to change

- `AppSecrets.cs` — contains EVE API credentials (gitignored)
- The manual service wiring in `App.OnStartup()` — this is intentional, not a missing DI container
- `Program.cs` entry point — Velopack hooks must run before WPF starts

## Testing approach

Tests cover pure-logic components that don't need WPF or network access:
- Ship classification (`ShipRoleClassifier`)
- Boost charge matching (`BoostChargeCatalog`)
- Wormhole mass checks (`WormholeMass`)
- MOTD parsing (`MotdParser`)
- Alert model properties (`FcAlert`)
- Intel entry properties (`IntelEntry`)
- After-action report (`SessionLog`)

When adding new testable logic, prefer putting it in Services or Models (not ViewModels) so it
can be tested without WPF dependencies.
