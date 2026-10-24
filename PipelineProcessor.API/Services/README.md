# Services: SRP and Dependency Inversion

This folder contains the application's **ports**, **adapters**, and **pure helpers**:

```text
Services/
├── IFileStorage.cs        port   - contract for reading/writing uploaded files
├── LocalFileStorage.cs    adapter - writes files to local disk (development)
├── R2FileStorage.cs       adapter - writes files to Cloudflare R2
├── IJobScheduler.cs       port   - contract for enqueuing background jobs
├── QuartzJobScheduler.cs  adapter - implements the port with Quartz.NET
├── IliRowParser.cs        pure helper - spreadsheet bytes -> rows + errors
└── AnomalyQueryService.cs application service - read-side anomaly queries
```

The two principles below explain why the code is split this way.

---

## 1. Single Responsibility Principle (SRP)

> **A class should have one reason to change.**

SRP is not "one method per class". It means each class should own **one kind of
decision**, so a change in one part of the system does not force changes in
unrelated classes.

| Type | Its one responsibility | It changes when... |
|---|---|---|
| `IFileStorage` | Define what the application needs from file storage. | The storage contract itself changes (for example, adding delete). |
| `LocalFileStorage` | Store bytes in a local directory. | The local development storage layout changes. |
| `R2FileStorage` | Translate storage calls into Cloudflare R2/S3 calls. | R2 credentials, bucket setup, or the S3 SDK change. |
| `IJobScheduler` | Define what the application needs from a job scheduler. | The job-enqueueing contract changes. |
| `QuartzJobScheduler` | Translate the port into Quartz job and trigger objects. | Quartz configuration or scheduling policy changes. |
| `IliRowParser` | Turn spreadsheet bytes into validated rows and row errors. | The vendor file format or validation rules change. |
| `AnomalyQueryService` | Own the read-side rules for anomalies, digs, and GeoJSON. | "Latest run", filtering, paging, or sorting rules change. |

### 1.1 Storage is separate from scheduling

`RunsController` needs two completely different infrastructure services:

1. somewhere to put the uploaded `.xlsx`, and
2. a way to start background work.

Those concerns are kept in two separate abstractions:

```csharp
IFileStorage   -> LocalFileStorage or R2FileStorage
IJobScheduler  -> QuartzJobScheduler
```

Changing how files are stored does not touch scheduling, and changing the
scheduler does not touch file storage.

### 1.2 Parsing is separate from saving

`IliRowParser` only does one job:

```text
bytes in -> parsed rows + rejected rows out
```

It has **no database, no HTTP, no Quartz, and no file system** dependencies.
That makes it a pure function, which is the easiest kind of code to unit test.

`IliImportJob` is responsible for the workflow around the parser:

```text
read file -> call parser -> save anomalies -> save errors -> update run status
```

The job does not contain the column names or validation rules; the parser does.
The parser does not know about `IliRun` or `Anomaly` entities; the job maps the
parser's records to entities.

### 1.3 Read queries are separate from controllers

Three endpoints need the same rule: "use the latest completed run and its latest
assessment". If that rule lived in each action, it would be copied three times
and could drift.

`AnomalyQueryService` owns that rule once:

```text
ResolveRunAsync             -> latest completed run (or a requested run)
ResolveAssessmentIdAsync    -> latest assessment for that run
GetAnomaliesAsync           -> filter + page
GetDigsAsync                -> ERF >= 1, sorted by urgency
GetForGeoJsonAsync          -> bounding-box query for the map
```

The controller only translates HTTP input into calls and results into HTTP
responses.

### 1.4 What stays outside this folder

SRP also means knowing what does **not** belong here:

| Type | Responsibility |
|---|---|
| `RunsController` | HTTP request/response handling and orchestration. |
| `AnomaliesController` | HTTP routing, query parameters, and GeoJSON response shaping. |
| `PipelinesController` | HTTP handling for pipeline registration and lookup. |
| `IliImportJob` | Coordinate the import steps; call the parser and the database. |
| `Program.cs` | Composition root: choose and wire concrete implementations. |

This is why the controller is called **thin**. It does not parse spreadsheets,
calculate ERF, or know how R2 works.

---

## 2. Dependency Inversion Principle (DIP)

> **High-level policy should not depend on low-level details. Both should depend
> on abstractions. Abstractions should not depend on details; details should
> depend on abstractions.**

### 2.1 The dependency rule

The important direction is the **source-code dependency**, not the runtime call:

```text
        high-level policy                    abstractions                 low-level details

        RunsController  ───────────────►  IFileStorage  ◄──────────────  LocalFileStorage
              │                                                          R2FileStorage
              │
              └─────────────────────────►  IJobScheduler ◄─────────────  QuartzJobScheduler

        IliImportJob    ───────────────►  IFileStorage
        IliImportJob    ───────────────►  IliRowParser
        IliImportJob    ───────────────►  PipelineDbContext
```

- `RunsController` mentions `IFileStorage` and `IJobScheduler`, **not**
  `R2FileStorage` or Quartz.
- `IliImportJob` mentions `IFileStorage`, **not** R2 or local disk.
- `R2FileStorage` is the only service class that imports the AWS S3 SDK;
  `Program.cs` only constructs the `AmazonS3Client` that is injected into it.
- `QuartzJobScheduler` is the only service that builds Quartz jobs and triggers.
- `Program.cs` is allowed to know the concrete classes because it is the
  **composition root**. Somebody has to choose the implementations.

At runtime the call still goes from the controller to the adapter, but the
controller's compiled code only knows the interface. The direction of
**knowledge** points inward, toward the abstraction.

### 2.2 Constructor injection

Dependencies are passed in through constructors instead of being created inside
the class:

```csharp
public RunsController(
    PipelineDbContext pipelineDbContext,
    IFileStorage fileStorage,
    IJobScheduler jobScheduler,
    ILogger<RunsController> logger)
```

```csharp
public IliImportJob(
    PipelineDbContext dbContext,
    IFileStorage fileStorage,
    ILogger<IliImportJob> logger)
```

This gives three benefits:

1. The class cannot silently choose R2 or Quartz on its own.
2. A test can pass a fake implementation.
3. `Program.cs` remains the single place that decides which concrete class is
   used in each environment.

### 2.3 The composition root chooses the adapter

`Program.ConfigureFileStorage` is the decision point:

```csharp
if (!string.IsNullOrWhiteSpace(r2Options.BucketName))
{
    // Production: store uploads in Cloudflare R2.
    builder.Services.AddSingleton<IFileStorage, R2FileStorage>();
}
else
{
    // Development: store uploads in a local folder.
    builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(rootPath));
}
```

The rest of the application never asks "are we using R2 or local disk?". It only
asks `IFileStorage` to write or read bytes. Switching storage is a configuration
and composition-root change, not a controller or job change.

The same idea applies to Quartz:

```csharp
builder.Services.AddScoped<IJobScheduler, QuartzJobScheduler>();
```

If Quartz is replaced later, a new `IJobScheduler` adapter can be registered in
`Program.cs`. `RunsController` stays unchanged.

### 2.4 Dependency Injection is not the same as Dependency Inversion

These terms are related but different:

| Term | Meaning |
|---|---|
| **Dependency Injection (DI)** | A technique: dependencies are passed in (usually via a constructor) instead of constructed internally. |
| **Dependency Inversion (DIP)** | A design principle: depend on abstractions, not concrete details. |

This project uses **constructor injection** to implement **Dependency
Inversion** at the infrastructure seams:

- `IFileStorage` is the abstraction for file storage.
- `IJobScheduler` is the abstraction for background job scheduling.
- `IAmazonS3` (inside `R2FileStorage`) is an abstraction for the S3 client.

### 2.5 Object lifetimes

`Program.cs` also decides how long each object lives:

| Registration | Lifetime | Why |
|---|---|---|
| `PipelineDbContext` | Scoped | One unit of work per HTTP request or job execution. |
| `IFileStorage` | Singleton | The storage adapter is stateless and can be shared. |
| `IJobScheduler` | Scoped | It resolves the shared Quartz scheduler per operation. |
| `IliImportJob` | Scoped | Quartz creates a DI scope for each job execution. |
| `AnomalyQueryService` | Scoped | It uses the scoped `PipelineDbContext`. |

### 2.6 What this buys us

- **Swappable infrastructure.** Local disk in development and R2 in production
  use the same application code.
- **Testable seams.** Tests can pass:
  - an `InMemoryFileStorage` dictionary instead of R2,
  - a `RecordingJobScheduler` spy that records enqueued jobs,
  - an in-memory EF Core database instead of MySQL.
- **A pure calculation/parsing core.** `IliRowParser` needs no test doubles at
  all.
- **Fewer surprises.** A controller cannot accidentally open an unmanaged
  `AmazonS3Client` or hard-code a database connection.

---

## 3. Walking through a request

Here is the upload flow with the SRP and DIP roles labelled:

```text
POST /pipelines/{pipelineId}/runs
        │
        ▼
RunsController                          (HTTP policy)
  ├─ PipelineDbContext                  (check the pipeline exists)
  ├─ IFileStorage.WriteAsync            (port)
  │      └─ LocalFileStorage / R2       (adapter chosen by Program.cs)
  ├─ PipelineDbContext.SaveChangesAsync (create IliRun)
  └─ IJobScheduler.EnqueueAsync         (port)
         └─ QuartzJobScheduler          (adapter)
                │
                ▼
        Quartz hosted service
                │
                ▼
        IliImportJob                    (import workflow)
          ├─ IFileStorage.ReadAsync     (port)
          ├─ IliRowParser.Parse         (pure logic)
          └─ PipelineDbContext          (save Anomalies / ImportErrors)
```

Each box has one main reason to change. Infrastructure details sit behind ports.

---

## 4. What changes when...

| Change | Files that should change | Files that should not |
|---|---|---|
| Move uploads from local disk to R2 | `Program.cs` configuration | `RunsController`, `IliImportJob` |
| Replace Quartz with another scheduler | New `IJobScheduler` adapter, `Program.cs` | `RunsController`, `IliImportJob` |
| Add a new spreadsheet column | `IliRowParser`, its tests | Controllers, storage adapters |
| Change the "latest run" rule | `AnomalyQueryService` | Controllers, storage adapters |
| Change the dig-list sort order | `AnomalyQueryService` | Parser, storage adapters |
| Change the HTTP status code for validation | `PipelinesController` / `RunsController` | Parser, storage, scheduler |

If a change forces edits in many unrelated files, that is a sign SRP is being
violated somewhere.

---

## 5. Trade-offs and possible improvements

This project applies DIP where it matters most (external infrastructure), but it
is not maximally abstract. Two deliberate trade-offs:

1. **`IJobScheduler` still mentions Quartz's `JobDataMap` and `IJob`.**
   Strictly speaking, that leaks a Quartz type into the port. A stricter port
   would accept something framework-neutral, such as
   `IReadOnlyDictionary<string, object?>` and a custom `IPipelineJob` marker
   interface, then let `QuartzJobScheduler` build the `JobDataMap` internally.
   This is a good exercise if you want to see the leak disappear.

2. **Services use `PipelineDbContext` directly instead of a repository
   interface.**
   EF Core's `DbContext` is already an abstraction over the database, and it is
   injected, so controllers and jobs do not construct connections. An
   `IAnomalyRepository` could be introduced if the project needed to support
   multiple database engines or unit-test query logic without EF at all. For
   this project, the direct `DbContext` keeps the code small and still gives a
   useful testing seam (the provider can be swapped).

DIP does **not** mean "create an interface for every class". It means the
high-level code should not be tied to volatile infrastructure choices. File
storage and job scheduling are exactly those choices; a simple value object or
pure parser is not.
