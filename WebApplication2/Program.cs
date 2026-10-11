using Amazon.Runtime;
using Amazon.S3;
using Hangfire;
using Hangfire.Redis.StackExchange;
using Microsoft.EntityFrameworkCore;
using WebApplication2.DbContexts;
using WebApplication2.Models.Options;
using WebApplication2.Services;

namespace WebApplication2;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.

        builder.Services.AddControllers();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.Configure<R2Options>(
            builder.Configuration.GetSection("R2"));

        var r2Options = builder.Configuration
            .GetSection("R2")
            .Get<R2Options>()!;

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
        
        builder.Services.AddScoped<IFileStorage, R2FileStorage>();
        builder.Services.AddScoped<ExcelProcessingJob>();

        builder.Services.AddHangfire(configuration =>
        {
            configuration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseRedisStorage(builder.Configuration.GetConnectionString("Redis"));
        });
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"));
        });
        builder.Services.AddHangfireServer();
        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.Database.Migrate();
        }

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.UseHangfireDashboard("/hangfire");

        app.MapControllers();

        app.Run();
    }
}