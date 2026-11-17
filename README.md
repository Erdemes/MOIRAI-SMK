# MOIRAI — Flight-Test Scheduling & What-If Tool

MOIRAI is a flight-test campaign planner. You enter your resources (test pilots,
flight-test engineers, aircraft) and flight-test campaigns, and it produces a
deterministic, day-by-day schedule of **sorties** (real flights) and **rehearsals**
(simulator sessions) that respects FTOM (Flight Test Organization Manual) duty/flight
limits. Its primary purpose is interactive **what-if analysis**: *"if I had one more
pilot / worse weather / a tighter deadline, what happens?"*

It is a C# / .NET 10 + [Avalonia](https://avaloniaui.net/) MVVM port of a MATLAB
prototype. The scheduling engine is pure and UI-independent.

---

## Features

### Resource & campaign management
- CRUD for **pilots**, **engineers** (with 4-char short names shown on the schedule),
  **aircraft** (tail number = id), and **campaigns**.
- Per-campaign: total/per-sortie test points, start & target dates, compatible aircraft,
  assigned crew, expected sortie/rehearsal hours, single-aircraft preference, and
  **prerequisites** (campaign B can require campaign A to finish first).
- Referential integrity: a resource used by a campaign cannot be deleted.

### Deterministic scheduler (FTOM rules)
- Greedy, deadline-ordered, day-by-day forward pass.
- Flight-hour caps (daily / 7-day / 28-day / yearly) and duty-hour caps
  (daily / 7-day / 14-day / 28-day) per person.
- Minimum rest between duty days, **rule 19** (max consecutive flying days), and an
  optional flight-duty-time cap.
- **Crew load balancing** by recent (rolling-window) duty hours; **aircraft** balanced
  across the fleet or locked to a single tail per campaign.
- **Working-day rehearsals**: each sortie needs a rehearsal by the same crew within
  N working days before it (weather does not apply to the simulator).
- Start-date gating and campaign prerequisites.

### What-if controls
- Flyable-day model (rate-based fraction over a repeating pattern, or explicit days),
  working calendar (weekend days + holidays), planning horizon, and **every FTOM limit**
  are editable. Pick which campaigns to include in a run.

### Outputs
- **Results table** of sorties (date, campaign, aircraft, pilot, engineers, rehearsal).
- **Per-person workload** (sorties, flight hours, duty hours, rehearsals).
- **Campaign details** (dates, aircraft & crew actually flown, status: Met / Late / Incomplete).
- **Custom-drawn Gantt** with an aircraft panel and a crew panel, day shading
  (holiday > weekend > not-flyable), per-campaign deadline lines, and hover tooltips
  showing full sortie/rehearsal details.
- **Multi-sheet Excel export** (Summary, Sorties, Aircraft-Calendar, Crew-Calendar)
  via ClosedXML.
- Diagnostics explain why a campaign was not completed (e.g. *blocked by an unfinished
  prerequisite*).

### Persistence
- The whole scenario is a single JSON file. **File → New / Open / Save As** plus an
  auto-saved working file (`moirai.json`) next to the executable.

---

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

## Quick start

1. Launch the app (or **File → Open Scenario…** and pick `sample-scenario.json`).
2. Add a few **pilots** and **engineers** (give them short names), some **aircraft**,
   and one or more **campaigns** (test points, dates, assigned crew/aircraft).
3. Go to **Plan**, set the horizon, the flyable rate and any FTOM limits, choose which
   campaigns to include, and click **Generate plan**.
4. Inspect the **Gantt**, the workload and campaign-detail summaries, and the schedule
   table. Hover a Gantt box for details.
5. Use **Export to Excel…** for a multi-sheet workbook, or **File → Save Scenario As…**
   to keep the scenario.

A ready-made `sample-scenario.json` (3 aircraft, 5 pilots, 6 engineers, 5 campaigns
with deliberate contention) is included.

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

The engine takes a `Scenario` and returns a `Schedule` with no I/O and no mutation of
its input (`ISchedulerService.Run`). The UI and persistence layers depend on the engine,
never the other way around.

## Status

200+ passing unit tests, including an end-to-end reference scenario that asserts
deadline compliance, crew/aircraft balancing, working-day rehearsals, and the what-if
drop in flyability.

## Tech

.NET 10 · Avalonia 12 · CommunityToolkit.Mvvm · System.Text.Json · ClosedXML · xUnit
