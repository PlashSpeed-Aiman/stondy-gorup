# PipelineProcessor API: Student Guide

This is a small ASP.NET Core 8 API for a pipeline-integrity workflow. It accepts
an in-line inspection (ILI) spreadsheet, saves the defects, calculates an
**Estimated Repair Factor (ERF)** for every defect, and exposes the results as
paged JSON, a GeoJSON map layer, and a repair ("dig") list.

The engineering formula is **Modified ASME B31G**. The full domain explanation
and the five project milestones are in [`ROADMAP.md`](ROADMAP.md).

> This guide is the map. The source files contain the detailed comments. Read
> the files in the order below and the comments will build on each other.
>
> This repository also contains the earlier `WebApplication2` sample; its
> documentation is in [`WebApplication2/README.md`](WebApplication2/README.md).

Paths in the reading-order tables are relative to `PipelineProcessor.API/`
unless they start with `PipelineProcessor.Persistence/` or `tools/`.

---

## 1. What the API does

There are four important nouns:

| Noun | Meaning in this project |
|---|---|
| **Pipeline** | A physical pipe: diameter, wall thickness, steel strength (SMYS), maximum operating pressure (MAOP), and a map route. |
| **ILI run** | One pass of an inspection tool, uploaded as one `.xlsx` file. |
| **Anomaly** | One defect row from that file: chainage, latitude/longitude, depth, and length. |
| **Assessment** | A calculation snapshot for one run. It stores the MAOP used and one result per anomaly. |

A **defect** is a corrosion pit. The steel wall is thinner there, so the pipe
can burst at a lower pressure. The assessment calculates:

```text
flowStress    = SMYS + 69
z             = L² / (D · t)
M             = z <= 50 ? sqrt(1 + 0.6275·z − 0.003375·z²)
                          : 0.032·z + 3.3
failStress    = flowStress · (1 − 0.85·d/t) / (1 − 0.85·(d/t)/M)
failPressure  = 2 · failStress · t / D
safePressure  = failPressure · 0.72
ERF           = MAOP / safePressure
```

- `D` = outside diameter in mm.
- `t` = wall thickness in mm.
- `d` = defect depth in mm. The spreadsheet stores `depth_pct`, so the job
  converts it: `d = depth_pct / 100 · t`.
- `L` = defect length in mm.
- `SMYS` and `MAOP` are in MPa.

**ERF ≥ 1 means repair is required.** An ERF below 1 means the defect is
currently acceptable. The `/digs` endpoint returns only ERF ≥ 1, most urgent
first.

Growth rate and remaining life are **not implemented yet**. They are
milestone 4 in the roadmap, so the growth columns in `AnomalyResults` stay
`null`.

### Request flow at a glance

```text
Client
  │
  │  POST /pipelines/{id}/runs  (multipart .xlsx)
  ▼
RunsController
  ├─ stores the file through IFileStorage
  ├─ inserts IliRun { Status = Importing }
  └─ asks IJobScheduler to run IliImportJob
  │
  ▼
IliImportJob
  ├─ reads the file
  ├─ calls IliRowParser (pure parsing)
  ├─ saves Anomalies + ImportErrors in batches
  ├─ sets IliRun { Status = Assessing, Progress = 90 }
  └─ enqueues AssessmentJob
  │
  ▼
AssessmentJob
  ├─ loads the pipeline and its anomalies
  ├─ calls IntegrityCalculator (pure B31G maths)
  ├─ saves Assessment + AnomalyResults
  └─ sets IliRun { Status = Completed, Progress = 100 }

Client polls GET /runs/{id}/status.
Client then reads /anomalies, /anomalies.geojson, and /digs.
```

The database status progresses like this:

```text
Importing ──► Assessing ──► Completed
    │             │
    └─────────────┴────────► Failed
```

The public status endpoint deliberately simplifies `Importing` and `Assessing`
to `"processing"`. The database keeps the more precise states for debugging.

### Endpoints

| Method | Route | What it returns |
|---|---|---|
| `POST` | `/pipelines` | Registers a pipeline. `201 Created`. |
| `GET` | `/pipelines/{id}` | One pipeline, or `404`. |
| `PUT` | `/pipelines` | Placeholder for a future update endpoint. |
| `POST` | `/pipelines/{id}/runs` | Accepts a spreadsheet, returns `runId` and a status URL immediately. |
| `GET` | `/runs/{id}/status` | Polling endpoint: status, progress percentage, and error message. |
| `GET` | `/pipelines/{id}/anomalies` | Paged anomalies with `minErf`, `fromM`, `toM`, `page`, `pageSize`, and optional `runId`. |
| `GET` | `/pipelines/{id}/anomalies.geojson` | GeoJSON `FeatureCollection` for a map, optionally limited by `bbox`. |
| `GET` | `/pipelines/{id}/digs` | Repair list: only ERF ≥ 1, sorted by ERF descending. |

The three anomaly endpoints are three views of the same data:

- `/anomalies` is for tables and exports.
- `/anomalies.geojson` is for map clients such as Leaflet or QGIS.
- `/digs` is the short, urgent list an engineer takes to the field.

---

## 2. The one-sentence architecture

> Controllers translate HTTP, application services own use-case rules, jobs
> coordinate background work, pure helpers do the maths and parsing, and
> `PipelineDbContext` talks to MySQL.

The dependency direction is:

```text
Controllers ──► application services ──► PipelineDbContext
     │                    │
     │                    └──► pure helpers
     └──► ports (IFileStorage, IJobScheduler) ◄── adapters
```

`Program.cs` is the **composition root**. It is the one place that chooses
MySQL, Quartz, R2 or local disk, and the object lifetimes.

---

## 3. Reading order

Read these in order. The comments in each file assume you have seen the
previous ones.

Quick version:

```text
ROADMAP
  -> entities -> PipelineDbContext -> DTOs
  -> Program
  -> PipelinesController
  -> IFileStorage / adapters -> IJobScheduler / adapter
  -> RunsController -> IliRowParser -> IliImportJob
  -> IntegrityCalculator -> IliRunStateService -> AssessmentJob
  -> AnomalyQueryService -> AnomaliesController
  -> migrations, appsettings, .http, sample-data tool
```

### Pass 1: the problem and the data

| # | File | Why read it here |
|---|---|---|
| 1 | [`ROADMAP.md`](ROADMAP.md) | The problem, vocabulary, formula, database guidelines, and milestones. |
| 2 | `PipelineProcessor.Persistence/Entities/*.cs` | The database shape: `Pipeline`, `IliRun`, `Anomaly`, `ImportError`, `Assessment`, `AnomalyResult`. |
| 3 | `PipelineProcessor.Persistence/PipelineDbContext.cs` | How entities become tables: keys, foreign keys, indexes, and delete behaviour. |
| 4 | `Models/*.cs` | The HTTP shape: request DTOs, response DTOs, paged results, and GeoJSON objects. |

At this point, you should be able to explain the difference between an
`Anomaly` (raw input from the file) and an `AnomalyResult` (calculated output).

### Pass 2: starting the application

| # | File | Why read it here |
|---|---|---|
| 5 | `Program.cs` | The composition root: DI registrations, MySQL, Quartz, file-storage choice, migrations, middleware. |
| 6 | `appsettings.json` | The MySQL connection string, local storage path, and empty R2 settings. |
| 7 | `PipelineProcessor.Persistence/Migrations/20261023151451_Initial.cs` | The actual SQL schema, including the auto-increment anomaly key. |

After this pass, you should know which concrete object is used when the code
asks for `IFileStorage`, and why `DbContext` is scoped.

### Pass 3: the write path (upload to ERF)

| # | File | Why read it here |
|---|---|---|
| 8 | `Controllers/PipelinesController.cs` | The simplest controller: validate, map DTO to entity, save, return `201`. |
| 9 | `Services/IFileStorage.cs`, `Services/LocalFileStorage.cs`, `Services/R2FileStorage.cs` | A port and its two adapters. |
| 10 | `Services/IJobScheduler.cs`, `Services/QuartzJobScheduler.cs` | The scheduling port and the Quartz adapter. |
| 11 | `Controllers/RunsController.cs` | Upload, store the file, create the run, enqueue the job, and poll. |
| 12 | `Services/IliRowParser.cs` | Pure spreadsheet parsing and row validation. |
| 13 | `Jobs/IliImportJob.cs` | The import workflow: read, parse, batch save, update status, enqueue assessment. |
| 14 | `Services/IntegrityCalculator.cs` | The pure Modified B31G formula and input validation. |
| 15 | `Services/IliRunStateService.cs` | The one place that changes a run's status, progress, and error. |
| 16 | `Jobs/AssessmentJob.cs` | The assessment workflow: load, calculate, batch save, complete or fail. |

### Pass 4: the read path (queries and map)

| # | File | Why read it here |
|---|---|---|
| 17 | `Services/AnomalyQueryService.cs` | The read-side rules: latest completed run, latest assessment, filters, paging, digs, and bounding boxes. |
| 18 | `Controllers/AnomaliesController.cs` | The three read endpoints and the GeoJSON envelope. |

### Pass 5: supporting material

| # | File | Why read it here |
|---|---|---|
| 19 | [`PipelineProcessor.API/Services/README.md`](PipelineProcessor.API/Services/README.md) | A deeper explanation of SRP and Dependency Inversion in this code. |
| 20 | `PipelineProcessor.http` | Ready-to-run example requests. |
| 21 | `tools/generate_ili_data.py` | Generates the sample pipeline and two inspection spreadsheets. |

---

## 4. Patterns used in this code

A pattern is a named solution to a recurring design problem. These are the ones
you will see.

| Pattern | Where | What it means here |
|---|---|---|
| **Layered architecture** | API and Persistence projects | HTTP and workflow code is separate from database mapping. |
| **MVC / controller** | `Controllers/` | A controller receives an HTTP request and returns an HTTP response. |
| **Thin controller (Humble Object)** | All controllers | The controller validates and translates; the real work is in services and jobs. This makes the hard parts testable without HTTP. |
| **Application service** | `AnomalyQueryService`, `IliRunStateService` | A class that owns one application use case or rule and is called by controllers/jobs. |
| **Ports and adapters (hexagonal)** | `IFileStorage`, `IJobScheduler` and their implementations | The application depends on a small interface; infrastructure plugs in behind it. |
| **Dependency Injection (DI)** | Constructors everywhere | Objects receive their dependencies instead of creating them. |
| **Dependency Inversion (DIP)** | Ports and `Program.cs` | High-level code depends on abstractions; the composition root chooses details. |
| **Composition root** | `Program.cs` | One place constructs and wires the concrete objects. |
| **Background job / job chain** | `Jobs/` | Long work runs outside the HTTP request; the import job hands off to the assessment job. |
| **Pure function / domain calculator** | `IntegrityCalculator`, `IliRowParser` | No database, network, clock, or logger. Same input, same output. Easy to test. |
| **DTO (Data Transfer Object)** | `Models/` | A class whose job is to define the JSON shape, not the database shape. |
| **Unit of Work** | `PipelineDbContext` | One `SaveChanges` call commits a set of related changes as one unit. |
| **Batch processing / change tracker** | `IliImportJob`, `AssessmentJob` | Save 1,000 rows at a time, then call `ChangeTracker.Clear()` so memory does not grow forever. |
| **State machine (small)** | `IliRunStatus` and `IliRunStateService` | A run moves through explicit states; only one service writes those transitions. |
| **Read/write separation** | `AnomalyQueryService` vs jobs | Write workflows and read queries have different rules, so they live in different classes. |
| **Input/result separation** | `Anomaly` vs `AnomalyResult` | The spreadsheet values are never overwritten. Each assessment stores its own calculated results. |

### Patterns deliberately not used

- **Repository interface around EF Core.** `DbContext` is already a unit of
  work and a database abstraction, and it is injected. Adding
  `IAnomalyRepository` would help only if the project needed multiple database
  engines or wanted to unit-test query logic without EF. The trade-off is
  explained in [`PipelineProcessor.API/Services/README.md`](PipelineProcessor.API/Services/README.md).
- **CQRS with separate databases.** The project separates read and write
  *classes*, but both use the same MySQL database. Full CQRS would be
  over-engineering here.

---

## 5. What is a "service" file, and why "service"?

### A service is a role, not a C# keyword

The word **service** describes a relationship: a class provides a capability to
a caller and hides how that capability is implemented. It does not mean a
separate web service, a microservice, or a process.

In this project the `PipelineProcessor.API/Services/` folder contains four different roles:

| Role | Examples | What it does |
|---|---|---|
| **Application service** | `AnomalyQueryService`, `IliRunStateService` | Owns a use-case rule, such as "use the latest completed run" or "write status and progress together". |
| **Port (interface)** | `IFileStorage`, `IJobScheduler` | Says what the application needs, without naming a technology. |
| **Adapter** | `LocalFileStorage`, `R2FileStorage`, `QuartzJobScheduler` | Implements a port using a specific technology. |
| **Pure helper** | `IliRowParser`, `IntegrityCalculator` | A plain class or static class with no dependencies; it just transforms data. |

So not every file in `PipelineProcessor.API/Services/` is an application
service. The folder is organised by the role the type plays in the
architecture.

### Why call it a service instead of a helper or manager?

`AnomalyQueryService` says exactly what it provides: anomaly query operations.
`IliRunStateService` says exactly what it owns: run state.

Names like `Helper`, `Manager`, or `Utility` are vague. They tend to collect
unrelated methods and become a dumping ground. A service name should describe a
responsibility, not a level of importance.

### Controller vs service vs job vs entity

| Type | Job in the system | Example |
|---|---|---|
| **Controller** | Translates HTTP in and HTTP out. | `AnomaliesController` reads query parameters and returns JSON or GeoJSON. |
| **Application service** | Owns an application rule that can be called from more than one place. | `AnomalyQueryService` resolves the latest completed run and assessment. |
| **Job** | A scheduled entry point for background work. | `IliImportJob` runs after an upload. |
| **Entity** | Stores state in the database. | `Anomaly` is a row in the `Anomalies` table. |
| **Pure helper** | Calculates or transforms data with no infrastructure. | `IntegrityCalculator` turns depth and length into ERF. |

A controller may call a service. A job may call a service or a pure helper. A
service should not know about HTTP. A pure helper should not know about any of
them.

### Why register services in DI?

`Program.cs` registers services with lifetimes:

- **Scoped**: one instance per HTTP request or per job execution. Use this for
  anything that holds a `PipelineDbContext`.
- **Singleton**: one instance for the whole application. Use this for stateless
  infrastructure such as the file-storage adapter or the S3 client.
- **Static**: no registration at all, because the type has no state and no
  dependencies. `IntegrityCalculator` is static.

DI is what lets a test pass an in-memory database, a fake file store, or a
recording scheduler instead of the real infrastructure.

---

## 6. What is a job?

A **job** is a unit of work that runs outside the HTTP request. In this project
a job means a class that implements Quartz.NET's `IJob`.

Jobs are used because importing thousands of spreadsheet rows and assessing
them can take seconds or minutes. The HTTP request should not wait for that. The
controller saves the upload, enqueues a job, and returns `201 Created`
immediately. The client polls the run status.

### The two jobs

| Job | Runs when | Does |
|---|---|---|
| `IliImportJob` | After `POST /pipelines/{id}/runs` | Reads the file, parses rows, saves anomalies and import errors, sets `Assessing`, enqueues `AssessmentJob`. |
| `AssessmentJob` | After the import job succeeds | Loads the pipeline and anomalies, calls `IntegrityCalculator`, saves `Assessment` and `AnomalyResults`, sets `Completed`. |

This is a **job chain**: each job does one stage and then asks the scheduler for
the next stage. If assessment fails, the import is still complete and the run's
error explains where it stopped.

### `Execute` vs `RunAsync`

Quartz calls `Execute(IJobExecutionContext context)`. In this codebase,
`Execute` is deliberately tiny:

```csharp
public Task Execute(IJobExecutionContext context)
{
    return RunAsync(
        context.MergedJobDataMap.GetString("runId"),
        context.CancellationToken);
}
```

`RunAsync` contains the real unit of work and is public so an integration test
can call it directly without starting a Quartz scheduler. This is the Humble
Object idea again: keep the hard-to-test framework entry point thin.

### Job context and job data

When a controller enqueues a job, it passes values in a `JobDataMap`:

```csharp
new JobDataMap { ["runId"] = runId }
```

Quartz hands that map back to the job through `IJobExecutionContext`. The
context also supplies:

- `CancellationToken`: cancelled when the scheduler shuts down;
- the job's identity and fire time;
- access to the scheduler and other Quartz services.

The imported run id is the only data this project needs. The job could pass more
values, but it is usually better to pass an id and let the job load the current
state from the database.

### Job rules in this project

- A job **coordinates**; it does not contain parsing or maths.
- A job records success or failure on the `IliRun` row.
- A job throws `JobExecutionException` so Quartz records the failure.
- `IliImportJob` has `[DisallowConcurrentExecution]`, so two imports cannot
  execute at the same time (including the same run enqueued twice).
- The scheduler is currently **in-memory** (`RAMJobStore`). If the process
  restarts, queued jobs are lost. A production system would use a durable job
  store or recovery logic.

---

## 7. What are "context" objects and `DbContext`?

"Context" is an overloaded word in .NET. It usually means **the state and
services that belong to one operation**. The important ones here are:

| Context | Lifetime | What it carries |
|---|---|---|
| `PipelineDbContext` | One HTTP request or job execution | The EF Core database session: queries, tracked entities, and `SaveChanges`. |
| `IJobExecutionContext` | One firing of one job | The job's data map, cancellation token, fire time, and scheduler access. |
| `HttpContext` | One HTTP request | Request, response, headers, user, and request services. The controllers do not use it directly, but `ControllerBase` exposes it. |
| `JobDataMap` | One job definition/execution | A dictionary of values passed from the enqueuer to the job. |
| `CancellationToken` | Passed through one operation | A cooperative "please stop" signal. It is not a context object, but it travels with the operation. |
| `IServiceScope` | One unit of work | The DI scope that owns scoped services such as `PipelineDbContext`. |

### `PipelineDbContext`

`PipelineDbContext` is **not** the HTTP context. It is EF Core's representation
of a database session.

```csharp
public class PipelineDbContext : DbContext
{
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<IliRun> IliRuns => Set<IliRun>();
    public DbSet<Anomaly> Anomalies => Set<Anomaly>();
    // ...
}
```

What it does for you:

- **Querying:** `db.Anomalies.Where(...)` becomes SQL.
- **Tracking:** after you load or add an entity, EF remembers it and can detect
  changes.
- **Unit of work:** `SaveChangesAsync()` writes all pending changes in one
  transaction-like unit.
- **Mapping:** `OnModelCreating` defines keys, foreign keys, indexes, and delete
  behaviour.
- **Migrations:** the migration files are generated from this model.

Important rules:

- A `DbContext` is **scoped**. Do not share one across parallel requests or
  background jobs.
- It is **not thread-safe**. Use one context per request/job.
- It should be **short-lived**. Create it through DI, use it, and let the scope
  dispose it.
- Use `AsNoTracking()` for read-only queries. It avoids the cost of tracking
  objects that will not be changed.
- Call `ChangeTracker.Clear()` after large batches. Otherwise EF keeps every
  saved entity in memory and the job slows down.

### Why is it called a "context"?

It is the **context in which database work happens**: it holds the connection,
the model, the change tracker, and the current unit of work. It has nothing to
do with `HttpContext`, even though both are scoped to a request-like operation.

### `IJobExecutionContext` in one sentence

It is the context for **one firing of one job**: "which job am I, what data was
I given, and has someone asked me to stop?"

---

## 8. End-to-end walkthrough with file names

### Register a pipeline

1. `PipelinesController.CreatePipeline` receives `CreatePipelineDto`.
2. It validates diameter, wall thickness, SMYS, and MAOP.
3. It maps the DTO to a `Pipeline` entity.
4. `PipelineDbContext.SaveChangesAsync` inserts the row.
5. It returns `201 Created` with a `Location` header.

### Upload a run

1. `RunsController.CreateRun` checks that the pipeline exists.
2. It validates the uploaded file and `inspectionDate`.
3. It copies the upload into memory.
4. It calls `IFileStorage.WriteAsync`, using the new run id as the storage key.
5. It inserts `IliRun { Status = Importing, ProgressPct = 0 }`.
6. It calls `IJobScheduler.EnqueueAsync<IliImportJob>` with the run id.
7. It returns `201 Created` with `runId` and `statusUrl`.

### Import the spreadsheet

1. `IliImportJob.Execute` reads `runId` from the job data and calls `RunAsync`.
2. `IFileStorage.ReadAsync` returns the original bytes.
3. `IliRowParser.Parse` returns valid `ParsedAnomaly` rows and `IliRowError`
   rows. It does not touch the database.
4. `IliImportJob` saves valid rows in batches of 1,000.
5. It saves rejected rows to `ImportErrors`.
6. It sets the run to `Assessing` with progress 90.
7. It enqueues `AssessmentJob`.

### Calculate ERF

1. `AssessmentJob.Execute` reads the run id and calls `RunAsync`.
2. It loads the run and pipeline with `AsNoTracking`.
3. It counts the anomalies.
4. It creates an `Assessment` row, copying `MaopMpaUsed`.
5. It reads anomalies in batches of 1,000.
6. For each anomaly it converts `depth_pct` to `depthMm` and calls
   `IntegrityCalculator.Calculate`.
7. It saves `AnomalyResult` rows with `FailurePressureMpa` and `Erf`.
8. It sets the run to `Completed` with progress 100.

### Read the results

1. `AnomaliesController.GetAnomalies` calls `AnomalyQueryService`.
2. The query service resolves the latest completed run and its latest
   assessment.
3. It applies `minErf`, `fromM`, and `toM`, then pages the result.
4. `GetAnomaliesGeoJson` builds a `FeatureCollection` and sets the media type to
   `application/geo+json`. Coordinates are `[longitude, latitude]`.
5. `GetDigs` filters `Erf >= 1`, sorts by ERF descending, then by remaining life
   ascending.

---

## 9. Running the API

Prerequisites: .NET 8 SDK and MySQL. The repository has a `compose.yaml` with a
MySQL service. From the repository root:

```bash
docker compose up -d mysql
dotnet run --project PipelineProcessor.API
```

Configuration is in `PipelineProcessor.API/appsettings.json`:

- `ConnectionStrings:DefaultConnection` points at MySQL.
- `Storage:RootPath` is the local upload folder (`storage/runs` by default).
- `R2:*` switches to Cloudflare R2 when `BucketName` is non-empty. With an empty
  bucket name, the API uses `LocalFileStorage`, so local development needs no
  cloud credentials.

Swagger is available in Development at `/swagger`. Example requests are in
`PipelineProcessor.API/PipelineProcessor.http`. EF Core migrations are applied
automatically at startup when the provider is relational (MySQL).

Generate sample data from the repository root:

```bash
pip install openpyxl
python tools/generate_ili_data.py
```

Then use `tools/sample-data/pipeline.json` for `POST /pipelines` and upload
`tools/sample-data/ili_run_1.xlsx`.

Build the solution:

```bash
dotnet build WebApplication2.sln
```

There is no committed test project yet. The pure helpers
(`IliRowParser` and `IntegrityCalculator`) are intentionally shaped so they can
be tested without a database, HTTP, or a scheduler. The testing plan in
`ROADMAP.md` recommends MSTest.

---

## 10. Glossary

| Term | Meaning |
|---|---|
| **ILI** | In-line inspection: a tool travels through the pipe and reports defects. |
| **Anomaly** | One reported defect. |
| **Chainage** | Distance along the pipeline, in metres (`DistanceM`). |
| **ERF** | Estimated Repair Factor. ERF ≥ 1 means repair. |
| **SMYS** | Specified Minimum Yield Strength of the steel, in MPa. |
| **MAOP** | Maximum Allowable Operating Pressure, in MPa. |
| **Assessment** | A calculation run that produces one result per anomaly. |
| **DTO** | Data Transfer Object: the JSON shape used at the HTTP boundary. |
| **Port** | An interface that says what the application needs from the outside world. |
| **Adapter** | A class that implements a port with a specific technology. |
| **Composition root** | The startup code that chooses concrete implementations. |
| **Job** | A unit of background work, here a Quartz `IJob`. |

---

## 11. Where to go next

- Read [`PipelineProcessor.API/Services/README.md`](PipelineProcessor.API/Services/README.md) for SRP and Dependency Inversion in more detail.
- Read [`ROADMAP.md`](ROADMAP.md) for milestone 4 (growth rate and
  remaining life) and the testing plan.
- Add unit tests for `IntegrityCalculator` first. It is the safest place to
  start because it has no infrastructure dependencies.
