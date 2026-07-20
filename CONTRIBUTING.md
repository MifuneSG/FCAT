# Contributing to FCAT

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) with the Windows desktop workload
- Windows 10/11 (WPF is Windows-only)
- An EVE Online account (for live testing; demo mode works without one)

## Quick Start

```powershell
# Clone and build
git clone https://github.com/MifuneSG/FCAT.git
cd FCAT
dotnet build

# Run the app
dotnet run --project FCAT/FCAT.csproj

# Run tests
dotnet test
```

## Project Structure

```
FCAT/
├── Models/       Data classes, enums, ESI DTOs
├── Services/     Business logic, API clients, file I/O
├── ViewModels/   MVVM ViewModels (CommunityToolkit.Mvvm)
├── Views/        WPF XAML UserControls
└── App.xaml      Design system, DataTemplates, service wiring

FCAT.Tests/       xUnit test project
docs/
├── ARCHITECTURE.md   System architecture overview
└── specs/            BDD requirements (EARS syntax)
```

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full architecture overview, dependency
graph, and data flow documentation.

## Running Tests

```powershell
dotnet test                              # run all tests
dotnet test --filter "ClassName=ShipRoleClassifierTests"  # run a specific test class
dotnet test --verbosity detailed         # verbose output
```

Tests cover the pure-logic components: ship classification, boost charge matching, wormhole mass
checks, MOTD parsing, alert model properties, intel entry properties, and the after-action report.

## Architecture

FCAT follows the MVVM pattern:

- **Models** — Plain data, no logic beyond computed display properties
- **Services** — All business logic; manually wired in `App.OnStartup()` (no DI container)
- **ViewModels** — Presentation logic using `[ObservableProperty]` and `[RelayCommand]` source generators
- **Views** — Pure XAML presentation

Services are concrete classes (no interfaces). The dependency graph is explicit in `App.OnStartup()`.

## BDD Specifications

Requirements are documented in `docs/specs/` using the EARS (Easy Approach to Requirements Syntax)
format. Each spec maps to a set of unit tests and describes the expected behaviour of a component.

When adding a new feature:
1. Write the EARS specification first (in `docs/specs/`)
2. Write tests that verify the specification
3. Implement the feature

## EVE API Credentials

To test against the live EVE API you need ESI credentials:

1. Register an app at https://developers.eveonline.com/
2. Copy `FCAT/AppSecrets.example.cs` to `FCAT/AppSecrets.cs`
3. Fill in your client ID and callback URL

For development without credentials, use demo mode (the app's built-in synthetic fleet data).

## Code Style

- File-scoped namespaces
- Primary constructors where appropriate
- Collection expressions (`[item1, item2]`)
- No comments unless the WHY is non-obvious
- `[ObservableProperty]` / `[RelayCommand]` source generators for ViewModels
