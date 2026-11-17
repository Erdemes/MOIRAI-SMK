## 19 ÇORUMLU 19


## Requirements

- **.NET 10 SDK** (preview). Check with `dotnet --version`.
- Avalonia 12 and its dependencies are restored automatically via NuGet.
- Primarily developed/tested on Windows; Avalonia is cross-platform, so it should also
  run on Linux/macOS.

## Build & run

```bash
dotnet restore
dotnet build
dotnet run --project Moirai.App
```

Run the tests:

```bash
dotnet test MOIRAI.Tests/MOIRAI.Tests.csproj
```

---

## Project structure

```
MOIRAI.sln
├─ Moirai.Core/        UI-independent engine (no Avalonia reference)
│  ├─ Domain/          Resource, Pilot, Engineer, Aircraft, Campaign, Sortie, Rehearsal
│  ├─ Scheduling/      Scenario, ScenarioConfig, FlightCalendar, FlyableDayModel,
│  │                   Schedule, ISchedulerService, Scheduler
│  ├─ Persistence/     IRepository, JsonRepository (System.Text.Json)
│  └─ Reporting/       ExcelExporter (ClosedXML)
├─ Moirai.App/         Avalonia MVVM UI (ViewModels, Views, GanttControl)
└─ MOIRAI.Tests/       xUnit tests, incl. the reference integration scenario
```

