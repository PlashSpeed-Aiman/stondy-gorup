# WebApplication2

An ASP.NET Core 8 API that **simulates long-running calculation work**. Rather than making the client wait for a slow computation, the API accepts the work, immediately returns a **resource ID**, and lets the client poll that ID to find out whether the calculation is finished — and to fetch the result once it is.

The "calculation" is an Excel-to-GeoJSON conversion: an uploaded `.xlsx` file containing latitude/longitude columns is converted into a GeoJSON `FeatureCollection`. A deliberate one-minute delay stands in for the real heavy computation.

**Next study project:** [ROADMAP.md](ROADMAP.md) turns this into a pipeline integrity API that runs real corrosion-assessment maths inside the same upload → job → poll flow.

## Request lifecycle

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API
    participant R as Redis (Hangfire queue)
    participant W as Background worker
    participant S as R2 storage
    participant D as SQLite

    C->>A: POST /File (multipart .xlsx)
    A->>S: Write file under a new GUID key
    A->>D: Insert Geometry { Id, Status = Processing }
    A->>R: Enqueue ExcelProcessingJob
    A-->>C: 201 Created { id, resultUrl }
    R->>W: Dequeue job
    W->>S: Read file bytes
    W->>W: Extract lat/lon, build GeoJSON (+ simulated 60s work)
    W->>D: Update Status = Completed with Data, or Status = Failed with error
    C->>A: GET /CalculationResult/{id}
    A-->>C: 202 processing, 200 GeoJSON when done, 422 on failure
```

1. `POST /File` — `FileController` streams the upload through `IFileStorage.Write` to Cloudflare R2 under a fresh GUID key, inserts a `Geometry { Id, Status = Processing }` row, enqueues a Hangfire background job (`ExcelProcessingJob.Process(filePath)`), and returns immediately with the resource ID. If the enqueue itself fails, the row is marked `Failed` so the ID never polls forever.
2. Worker — Hangfire (backed by Redis) picks up the job: `IFileStorage.Read` fetches the bytes, `ExtractCoordinates` reads the first worksheet using header cells containing `lat` and `lon`/`lng`, and `BuildGeoJson` produces a WGS84 (EPSG:4326) `FeatureCollection`. `await Task.Delay(60000)` simulates the long calculation.
3. Persist — the GeoJSON is stored in SQLite via EF Core by updating the row to `Geometry { Data, Status = Completed }`.
4. Fail — any error (unreadable file, no lat/lon values, storage or database failure) updates the row to `Status = Failed` with the error message and fails the Hangfire job so it shows up in the dashboard. Failure is terminal: `[AutomaticRetry(Attempts = 0)]` disables Hangfire's automatic retries.
5. Poll — the result endpoints report `Processing` (`202`), `Completed` (`200` + GeoJSON), or `Failed` (`422` + error message); an unknown ID returns `404`.

## API reference

| Method | Route | Result | Purpose |
|---|---|---|---|
| `POST` | `/File` | `201 Created` | Accepts a multipart upload (form field `file`) and returns the new resource ID. |
| `GET` | `/CalculationResult/{id}` | `200` GeoJSON / `202` / `422` / `404` | Returns the finished result; `202 Accepted` with `{ "status": "processing" }` while the job runs; `422` with `{ "status": "failed", "error": "..." }` if it failed; `404` for an unknown ID. |
| `GET` | `/CalculationResult/{id}/status` | `200` / `404` JSON | Lightweight poll: `{ "status": "processing" }`, `{ "status": "completed" }`, or `{ "status": "failed", "error": "..." }`; `404` for an unknown ID. |
| `GET` | `/hangfire` | Dashboard | Background job dashboard: queues, retries, history. |

### Example

```bash
# Upload — returns the resource ID straight away
curl -k -i -F "file=@points.xlsx" https://localhost:7182/File
```

```json
{
  "id": "6b9f3c1e-0f5a-4d0e-9f2c-8b1d7a4e5c10",
  "resultUrl": "https://localhost:7182/CalculationResult/6b9f3c1e-0f5a-4d0e-9f2c-8b1d7a4e5c10"
}
```

```bash
# Poll until completed
curl -k https://localhost:7182/CalculationResult/6b9f3c1e-0f5a-4d0e-9f2c-8b1d7a4e5c10/status
# → { "status": "processing" }   ... later ...
# → { "status": "completed" }
# → or, if the calculation failed:
#   { "status": "failed", "error": "No latitude/longitude values found in file: ..." }

# Fetch the result (application/geo+json)
curl -k https://localhost:7182/CalculationResult/6b9f3c1e-0f5a-4d0e-9f2c-8b1d7a4e5c10
# → 422 { "status": "failed", "error": "..." } if the calculation failed
```

The `id` is the storage key assigned when the file was written (a GUID) and serves as the resource ID for all polling.

### Expected input format

- Only the first worksheet is read.
- A header cell containing `lat` selects the latitude column; one containing `lon` or `lng` selects the longitude column (case-insensitive).
- Rows below the header with numeric values in both columns become GeoJSON points.
- If no such columns (or no data rows) are found, the job fails the calculation with a descriptive error, and both endpoints report `failed`.

## Architecture

The goal of the structure is that the application core (endpoints and processing logic) knows nothing about Cloudflare R2, Redis, or SQLite — it only knows interfaces.

### Ports & Adapters (hexagonal architecture)

- **Port** — `Services/IFileStorage.cs` defines *what* the application needs: "a port for storing and retrieving files".
- **Adapter** — `Services/R2FileStorage.cs` implements *how*: Cloudflare R2 over the S3 API, and is explicitly documented as "the adapter for our `IFileStorage` port". `IAmazonS3` is itself another port, whose adapter is Amazon's `AmazonS3Client`, constructed once in the composition root.
- The controllers and the background job depend only on the port, never on the storage technology. Swapping R2 for local disk or another bucket service means writing one new adapter and changing a single DI registration in `Program.cs` — no changes in the core.

### Single Responsibility Principle

Every class has one reason to change:

| Class | Responsibility |
|---|---|
| `FileController` | HTTP upload handling and enqueueing the job — nothing else. |
| `CalculationResultController` | Answering result/status queries. |
| `ExcelProcessingJob` | The processing pipeline: read → extract → build GeoJSON → persist. |
| `R2FileStorage` | Translating the storage port into S3 calls. |
| `ApplicationDbContext` | Persistence mapping only. |
| `R2Options` | The shape of the R2 configuration section. |

### Dependency inversion

- High-level policy (controllers, job) depends on **abstractions** — `IFileStorage`, `ApplicationDbContext`, `ILogger<T>`, `IOptions<R2Options>` — all supplied through constructor injection. Nothing is `new`-ed up inside business code.
- Low-level details (the S3 client, Redis-backed Hangfire, SQLite) are constructed in the composition root, `Program.cs`, and wired via the DI container.
- `R2FileStorage` depends on the `IAmazonS3` interface rather than `AmazonS3Client`, so even the adapter depends on an abstraction.
- The result: dependencies point inward toward the interfaces, and the core can be unit-tested or re-hosted with different infrastructure without modification.

## Project layout

```
WebApplication2/
├── Controllers/
│   ├── FileController.cs              # upload → storage → enqueue → 201 with resource ID
│   └── CalculationResultController.cs # result / status polling
├── Services/
│   ├── IFileStorage.cs                # the storage port
│   └── R2FileStorage.cs               # R2 (S3 API) adapter
├── Jobs/
│   └── ExcelProcessingJob.cs          # background job + simulated 60s calculation
├── Entities/
│   ├── Geometry.cs                    # Id + GeoJSON payload + status/error
│   └── CalculationStatus.cs           # Processing / Completed / Failed
├── DbContexts/
│   └── ApplicationDbContext.cs        # EF Core / SQLite
├── Migrations/                        # applied automatically on startup
├── Models/
│   ├── CalculationResultDto.cs
│   └── Options/R2Options.cs           # configuration shape for R2
└── Program.cs                         # composition root: DI, Hangfire, EF, Swagger
```

## Configuration

| Setting | Used for | Default |
|---|---|---|
| `R2:AccountId`, `R2:AccessKeyId`, `R2:SecretAccessKey`, `R2:BucketName` | Cloudflare R2 (S3-compatible) object storage | empty — must be supplied |
| `ConnectionStrings:Redis` | Hangfire job storage | `localhost:6379` |
| `ConnectionStrings:DefaultConnection` | Application data (SQLite) | `Data Source=app.db` |

Secrets are never committed. For local development use .NET user secrets (stored outside the repository):

```bash
cd WebApplication2
dotnet user-secrets init
dotnet user-secrets set "R2:AccountId" "<account-id>"
dotnet user-secrets set "R2:AccessKeyId" "<key-id>"
dotnet user-secrets set "R2:SecretAccessKey" "<secret>"
dotnet user-secrets set "R2:BucketName" "<bucket>"
```

In containers, pass the same keys as environment variables using the standard double-underscore mapping: `R2__AccountId`, `R2__AccessKeyId`, `R2__SecretAccessKey`, `R2__BucketName`.

## Running locally

Prerequisites: .NET 8 SDK, Docker (for Redis), and R2 credentials.

```bash
# 1. Start Redis (Hangfire storage)
docker compose up -d redis

# 2. Configure R2 credentials (see Configuration above)

# 3. Run the API
dotnet run --project WebApplication2
```

- Swagger UI (Development): `https://localhost:7182/swagger` or `http://localhost:5043/swagger`
- EF Core migrations are applied automatically at startup; the SQLite database (`WebApplication2/app.db`) is created on first run and is git-ignored.

### Docker

```bash
docker compose up --build
```

This builds the API image and starts Redis. Provide the R2 settings to the `webapplication2` service as environment variables (e.g. via an `environment:` block or a git-ignored `.env` file).

## Tech stack

- .NET 8 / ASP.NET Core Web API, Swashbuckle (Swagger/OpenAPI)
- Hangfire + Redis — background job queue and dashboard
- EF Core + SQLite — result persistence, migrations
- AWSSDK.S3 — Cloudflare R2 access
- ClosedXML — Excel parsing; NetTopologySuite + GeoJSON4STJ — GeoJSON output

## Suggested improvements

Ideas to harden the current implementation. None are required for the API to work as documented above.

1. **Make file I/O async and streaming.** `IFileStorage` currently exposes synchronous `Write(byte[])` / `Read(string)`, implemented in `R2FileStorage` with blocking `.GetAwaiter().GetResult()` calls, and `FileController.Upload` buffers the whole file in memory (`CopyTo` + `ToArray()`) before uploading. Changing the port to `WriteAsync(Stream, CancellationToken)` / `ReadAsync(string, CancellationToken)` and passing `file.OpenReadStream()` straight to R2 removes the sync-over-async blocking and the duplicate in-memory copies. ASP.NET Core form streams are seekable, so the S3 SDK can still determine the content length. No API contract change.
2. **Run the R2 upload as a background job.** Today the request waits for the upload to finish before returning the resource ID. Enqueueing the upload with Hangfire makes the response constant-time, but it requires staging the file before responding (temp disk or database) and extending the job-state model (`processing` / `completed` / `failed` on `Geometry`) to cover the upload stage as well.
3. **Accept uploads via presigned URLs.** For large files, hand the client a presigned PUT URL so it uploads straight to R2 and then calls the API to enqueue processing. The API never touches the bytes (constant-time, no staging), at the cost of a two-step client flow.
4. **Clean up orphaned objects.** If `BackgroundJob.Enqueue` throws after a successful upload, or a calculation fails permanently, the R2 object is left behind (the port has no delete operation). Adding a `DeleteAsync` to the port would let the API remove files it no longer needs.
