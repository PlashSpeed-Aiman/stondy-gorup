# Study project: Pipeline Integrity API

The next thing we build on top of this API. It takes the upload → background job → poll → GeoJSON flow we already have and puts a real engineering problem inside it, in place of the fake 60-second delay.

## The problem in plain terms

Oil and gas travel through steel pipes that run for hundreds of kilometres. Over time the steel **corrodes**, and pits form where the wall gets thinner. A thin spot can burst if the pressure inside is too high.

To find these spots, operators send a robot called a **pig** through the pipe. It measures the wall as it goes. The inspection company then sends back a spreadsheet with one row per defect (an **anomaly**):

| Column | Meaning |
|---|---|
| `distance_m` | How far along the pipe the defect is (also called *chainage*) |
| `lat`, `lon` | Where it is on the map |
| `depth_pct` | How deep the pit is, as a % of the wall thickness |
| `length_mm` | How long the pit is along the pipe |

An engineer needs answers to three questions:

1. **Is this defect dangerous right now?**
2. **How fast is it getting worse?**
3. **When must we dig it up and repair it?**

Our API answers them automatically for every defect in the file.

## Words you'll see

| Term | Meaning |
|---|---|
| **OD** (`D`) | Outside diameter of the pipe, in mm |
| **WT** (`t`) | Wall thickness, in mm |
| **SMYS** | How strong the steel is (Specified Minimum Yield Strength), in MPa. Comes from the steel grade, e.g. X52 = 359 MPa, X65 = 448 MPa |
| **MAOP** | The highest pressure the pipe is allowed to run at (Maximum Allowable Operating Pressure), in MPa |
| **ILI run** | One pass of the pig, i.e. one uploaded spreadsheet |
| **ERF** | Estimated Repair Factor. A score where **≥ 1 means "repair needed"** |

## The maths (just functions, no engineering background needed)

We use **Modified ASME B31G**, an industry-standard formula. Given a pipe (`D`, `t`, `SMYS`) and a defect (depth `d` in mm, length `L` in mm):

```
flowStress = SMYS + 69                              // MPa
z          = L² / (D · t)
M          = z <= 50 ? sqrt(1 + 0.6275·z − 0.003375·z²)
                     : 0.032·z + 3.3                // "bulging factor"
failStress = flowStress · (1 − 0.85·d/t) / (1 − 0.85·(d/t) / M)
failPressure = 2 · failStress · t / D              // pressure that bursts the pipe
safePressure = failPressure · 0.72                  // safety factor
ERF          = MAOP / safePressure                  // ≥ 1 → repair
```

When we have **two ILI runs** of the same pipe, a few years apart:

```
growthRate    = (depthNow − depthBefore) / yearsBetween      // mm per year
remainingLife = (0.8·t − depthNow) / growthRate              // years until 80% wall loss
```

> Unit test these first. They are pure functions with no database and no HTTP, so they're the easiest place to start.

## What we'll build

**Entities:** `Pipeline` (D, t, grade, MAOP, route) → `IliRun` (date, file, status) → `Anomaly` (distance, location, depth, length, ERF, growth rate, remaining life).

| Method | Route | Does |
|---|---|---|
| `POST` | `/pipelines` | Register a pipeline and its properties |
| `POST` | `/pipelines/{id}/runs` | Upload an ILI spreadsheet. Returns a run ID straight away (like today's `/File`) |
| `GET` | `/runs/{id}/status` | Poll: processing / completed / failed |
| `GET` | `/pipelines/{id}/anomalies?minErf=1&fromM=&toM=` | Filter defects by danger level or by stretch of pipe |
| `GET` | `/pipelines/{id}/anomalies.geojson?bbox=` | Defects as a GeoJSON map layer |
| `GET` | `/pipelines/{id}/digs` | Repair list, sorted by most urgent |

## System architecture

The same building blocks as today's API, with new jobs and a pure calculation module added. Arrows point from the caller to what it depends on.

```mermaid
flowchart LR
    Client["Client<br/>(curl / Swagger / map UI)"]

    subgraph API["ASP.NET Core API (one process)"]
        direction TB
        Controllers["Controllers<br/>Pipelines · Runs · Anomalies"]
        subgraph Jobs["Hangfire workers"]
            Import["IliImportJob"]
            Assess["AssessmentJob"]
            Growth["GrowthJob"]
            Alert["AlertJob"]
        end
        Calc["Integrity calculator<br/>(pure B31G + growth maths)"]
        Ports["Ports<br/>IFileStorage · ApplicationDbContext"]
    end

    R2[("Cloudflare R2<br/>raw spreadsheets")]
    Redis[("Redis<br/>job queue")]
    DB[("SQLite<br/>pipelines · runs · anomalies")]
    Hook["Webhook receiver<br/>(e.g. a test endpoint)"]

    Client -->|HTTP| Controllers
    Controllers --> Ports
    Controllers -->|enqueue| Redis
    Redis -->|dequeue| Jobs
    Assess --> Calc
    Growth --> Calc
    Jobs --> Ports
    Ports --> R2
    Ports --> DB
    Alert -->|POST| Hook
```

**Rules to keep the design clean:**

- **Controllers stay thin.** They validate the request, save a row, enqueue a job and return. They never do the maths.
- **The calculator is pure.** No database, no HTTP, no files: numbers in, numbers out. That makes it easy to unit test, and it's the code that has to be right.
- **Jobs coordinate the work.** They load data through the ports, call the calculator, and save the results.
- **Each job does one step and then enqueues the next.** If one step fails, you can see where in the Hangfire dashboard.

## Data flow

What happens from upload to results, for one ILI run:

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API
    participant S as R2
    participant D as SQLite
    participant Q as Redis (Hangfire)
    participant W as Workers

    C->>A: POST /pipelines/{id}/runs (xlsx)
    A->>S: Store file under new run ID
    A->>D: Insert IliRun { Status = Importing }
    A->>Q: Enqueue IliImportJob(runId)
    A-->>C: 201 { runId, statusUrl }

    Q->>W: IliImportJob
    W->>S: Read file
    W->>W: Parse + validate rows
    W->>D: Insert Anomalies in batches (e.g. 1,000 rows)
    W->>Q: Enqueue AssessmentJob(runId)

    Q->>W: AssessmentJob
    W->>D: Load pipeline (D, t, SMYS, MAOP) + anomalies
    W->>W: B31G → ERF per anomaly
    W->>D: Save ERF, update progress %
    W->>Q: Enqueue GrowthJob (if an earlier run exists)

    Q->>W: GrowthJob
    W->>D: Load previous run's anomalies
    W->>W: Match by distance ± tolerance → growth rate, remaining life
    W->>D: Save results, IliRun.Status = Completed
    W->>Q: Enqueue AlertJob (if any ERF ≥ 1)

    C->>A: GET /runs/{id}/status
    A-->>C: { status, progress }
    C->>A: GET /pipelines/{id}/digs
    A-->>C: Anomalies sorted by urgency
```

The run's status moves through these states:

```mermaid
stateDiagram-v2
    [*] --> Importing
    Importing --> Assessing: rows saved
    Assessing --> Completed: ERF + growth saved
    Importing --> Failed: bad file / no valid rows
    Assessing --> Failed: calculation error
    Completed --> [*]
    Failed --> [*]
```

Each failure stores an error message, just like `Geometry.Status = Failed` does today.

## Milestones, and what each one teaches

| # | Build | Backend concept you learn |
|---|---|---|
| 1 | Pipelines + ILI upload, reusing `ExcelProcessingJob` and `IFileStorage` | Relational modelling, foreign keys, input validation with per-row error messages |
| 2 | B31G assessment job (replace `Task.Delay` with the real maths) | Background jobs doing real CPU work, batching large files, progress reporting, unit testing pure logic |
| 3 | Filtering, paging, GeoJSON map endpoint | Query design, pagination, database indexes, spatial queries |
| 4 | Compare two runs → growth rate + remaining life | Matching records between datasets (match by `distance_m` ± tolerance), keeping old results instead of overwriting them |
| 5 | Alert when ERF ≥ 1, audit log, user roles | Webhooks with retries, append-only logs, authentication and authorisation |

## Test data

There's no real pig data to download, so write a small script that generates a fake ILI spreadsheet: a straight pipeline route, a few thousand anomalies at random distances, mostly shallow (5–30 %) with a few deep ones (60–80 %). Make a second "run" by growing each depth by 0.1–0.5 mm/year so milestone 4 has something to compare.
