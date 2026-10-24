using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Jobs;
using PipelineProcessor.Models.Options;
using PipelineProcessor.Persistence;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor;

// ============================================================================
// Program
// ----------------------------------------------------------------------------
// This is the composition root of the application. It is the one place that is
// allowed to know about concrete infrastructure:
//
//   - which database provider to use (MySQL/Pomelo),
//   - which file storage adapter to use (R2 or local disk),
//   - which job scheduler to use (Quartz),
//   - how long each service lives (scoped vs singleton).
//
// Controllers and jobs depend on interfaces such as IFileStorage and
// IJobScheduler. Program wires the real implementations into the DI container,
// then starts the web server. For a deeper explanation, see
// Services/README.md.
// ============================================================================

public class Program
{
    public static void Main(string[] args)
    {
        // Step 1: create the builder. It reads appsettings.json, environment
        // variables and command-line arguments into builder.Configuration, and
        // collects services in builder.Services.
        var builder = WebApplication.CreateBuilder(args);

        // --------------------------------------------------------------------
        // Step 2: register the web API services.
        // --------------------------------------------------------------------
        // AddControllers discovers the [ApiController] classes in this project
        // and enables model binding, validation and JSON serialization.
        builder.Services.AddControllers();

        // Swagger/OpenAPI generates interactive documentation at /swagger.
        // AddEndpointsApiExplorer tells Swagger where the endpoints are;
        // AddSwaggerGen builds the OpenAPI document.
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // --------------------------------------------------------------------
        // Step 3: register the database.
        // --------------------------------------------------------------------
        // AddDbContext registers PipelineDbContext with a scoped lifetime: one
        // context per HTTP request or per background-job execution.
        //
        // UseMySql selects Pomelo, the EF Core provider for MySQL. The server
        // version is supplied explicitly instead of using AutoDetect, because
        // AutoDetect opens a database connection during startup; a fixed version
        // lets the application start (and tests run) without MySQL already up.
        builder.Services.AddDbContext<PipelineDbContext>(options =>
            options.UseMySql(
                builder.Configuration.GetConnectionString("DefaultConnection"),
                new MySqlServerVersion(new Version(8, 0, 36))));

        // --------------------------------------------------------------------
        // Step 4: register application services.
        // --------------------------------------------------------------------
        // Scoped services live for one request/job. The query service and the
        // run-state service use the scoped DbContext, so they must be scoped as
        // well. The two jobs are resolved by Quartz inside a per-job DI scope:
        // IliImportJob saves the spreadsheet rows, then AssessmentJob calculates
        // ERF with the pure IntegrityCalculator.
        builder.Services.AddScoped<AnomalyQueryService>();
        builder.Services.AddScoped<IliRunStateService>();
        builder.Services.AddScoped<IliImportJob>();
        builder.Services.AddScoped<AssessmentJob>();

        // This is the Dependency Inversion seam for background jobs:
        //   controller -> IJobScheduler -> QuartzJobScheduler -> Quartz.
        // The controller never mentions QuartzJobScheduler directly.
        builder.Services.AddScoped<IJobScheduler, QuartzJobScheduler>();

        // --------------------------------------------------------------------
        // Step 5: configure Quartz.NET.
        // --------------------------------------------------------------------
        // AddQuartz registers the scheduler and its services. No persistent job
        // store is configured, so Quartz uses its in-memory RAMJobStore: jobs
        // are lost if the process restarts. That is acceptable for this study
        // project; a production system would use a durable store or recover
        // unfinished runs at startup.
        builder.Services.AddQuartz(configurator =>
        {
            configurator.SchedulerId = "PipelineProcessor";
            configurator.SchedulerName = "PipelineProcessor";
        });

        // The hosted service starts the scheduler with the web application and
        // stops it on shutdown. WaitForJobsToComplete = true gives running
        // imports a chance to finish instead of being cut off immediately.
        builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        // --------------------------------------------------------------------
        // Step 6: choose the file-storage adapter.
        // --------------------------------------------------------------------
        // The decision is kept in its own method so the controller and job only
        // ever see the IFileStorage interface.
        ConfigureFileStorage(builder);

        // --------------------------------------------------------------------
        // Step 7: build the application.
        // --------------------------------------------------------------------
        // From this point on the service registrations are fixed and the DI
        // container can create objects.
        var app = builder.Build();

        // --------------------------------------------------------------------
        // Step 8: apply database migrations at startup.
        // --------------------------------------------------------------------
        // Database.Migrate() needs a scope because DbContext is scoped, while
        // app.Services is the root provider. IsRelational() guards the call so
        // tests using a non-relational provider (for example EF Core InMemory)
        // can start the app without trying to run SQL migrations.
        //
        // In a production deployment you may prefer to run migrations as a
        // separate deployment step rather than on every application start.
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();

            if (dbContext.Database.IsRelational())
            {
                dbContext.Database.Migrate();
            }
        }

        // --------------------------------------------------------------------
        // Step 9: configure the HTTP request pipeline.
        // --------------------------------------------------------------------
        // Middleware runs in the order it is added. Swagger is enabled only in
        // Development so the API documentation is not exposed in production.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Redirect plain HTTP requests to HTTPS.
        app.UseHttpsRedirection();

        // Authorization middleware is included for the future milestone that
        // adds users and roles. There is no authentication configured yet, so
        // it currently lets requests through.
        app.UseAuthorization();

        // MapControllers connects the attribute-routed controller actions
        // (for example [HttpGet("anomalies")]) to the HTTP pipeline.
        app.MapControllers();

        // Step 10: start the web server and block until the process stops.
        app.Run();
    }

    /// <summary>
    /// Registers IFileStorage as either the R2 adapter or the local-disk
    /// adapter, depending on configuration.
    /// </summary>
    private static void ConfigureFileStorage(WebApplicationBuilder builder)
    {
        // Read the R2 section once to decide immediately whether R2 is
        // configured. Get<R2Options>() binds the JSON section to a C# object.
        var r2Options = builder.Configuration.GetSection("R2").Get<R2Options>() ?? new R2Options();

        // Also register the options object itself. R2FileStorage receives it as
        // IOptions<R2Options>, so it can read BucketName and credentials.
        builder.Services.Configure<R2Options>(builder.Configuration.GetSection("R2"));

        // A non-empty bucket name means "R2 is configured". In that case use the
        // R2 adapter; otherwise fall back to local disk so development works
        // without cloud credentials.
        if (!string.IsNullOrWhiteSpace(r2Options.BucketName))
        {
            // IAmazonS3 is itself a port. The factory creates one client and
            // the singleton lifetime reuses it for every upload instead of
            // creating a new HTTP client per request.
            builder.Services.AddSingleton<IAmazonS3>(_ =>
            {
                var credentials = new BasicAWSCredentials(
                    r2Options.AccessKeyId,
                    r2Options.SecretAccessKey);

                var config = new AmazonS3Config
                {
                    // R2 is S3-compatible but uses an account-specific endpoint.
                    ServiceURL = $"https://{r2Options.AccountId}.r2.cloudflarestorage.com",

                    // R2 requires path-style addressing rather than the
                    // bucket-in-the-hostname style used by some S3 setups.
                    ForcePathStyle = true,

                    // R2 uses "auto" as its signing region.
                    AuthenticationRegion = "auto"
                };

                return new AmazonS3Client(credentials, config);
            });

            // When a controller or job asks for IFileStorage, the container
            // supplies R2FileStorage. They do not know this choice was made.
            builder.Services.AddSingleton<IFileStorage, R2FileStorage>();
            return;
        }

        // Development fallback: store uploads under a local folder. The path
        // comes from configuration so it can be changed without editing code.
        var rootPath = builder.Configuration["Storage:RootPath"] ?? "storage/runs";
        builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(rootPath));
    }
}
