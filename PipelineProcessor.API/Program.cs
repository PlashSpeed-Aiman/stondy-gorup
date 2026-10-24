using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Jobs;
using PipelineProcessor.Models.Options;
using PipelineProcessor.Persistence;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddDbContext<PipelineDbContext>(options =>
            options.UseMySql(
                builder.Configuration.GetConnectionString("DefaultConnection"),
                new MySqlServerVersion(new Version(8, 0, 36))));

        builder.Services.AddScoped<AnomalyQueryService>();
        builder.Services.AddScoped<IliImportJob>();
        builder.Services.AddScoped<IJobScheduler, QuartzJobScheduler>();

        builder.Services.AddQuartz(configurator =>
        {
            configurator.SchedulerId = "PipelineProcessor";
            configurator.SchedulerName = "PipelineProcessor";
        });

        builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        ConfigureFileStorage(builder);

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();

            if (dbContext.Database.IsRelational())
            {
                dbContext.Database.Migrate();
            }
        }

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }

    private static void ConfigureFileStorage(WebApplicationBuilder builder)
    {
        var r2Options = builder.Configuration.GetSection("R2").Get<R2Options>() ?? new R2Options();
        builder.Services.Configure<R2Options>(builder.Configuration.GetSection("R2"));

        if (!string.IsNullOrWhiteSpace(r2Options.BucketName))
        {
            builder.Services.AddSingleton<IAmazonS3>(_ =>
            {
                var credentials = new BasicAWSCredentials(
                    r2Options.AccessKeyId,
                    r2Options.SecretAccessKey);

                var config = new AmazonS3Config
                {
                    ServiceURL = $"https://{r2Options.AccountId}.r2.cloudflarestorage.com",
                    ForcePathStyle = true,
                    AuthenticationRegion = "auto"
                };

                return new AmazonS3Client(credentials, config);
            });

            builder.Services.AddSingleton<IFileStorage, R2FileStorage>();
            return;
        }

        var rootPath = builder.Configuration["Storage:RootPath"] ?? "storage/runs";
        builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(rootPath));
    }
}
