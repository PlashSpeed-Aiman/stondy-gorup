using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Persistence.Entities;

namespace PipelineProcessor.Persistence;

public class PipelineDbContext : DbContext
{
    public PipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<IliRun> IliRuns => Set<IliRun>();
    public DbSet<Anomaly> Anomalies => Set<Anomaly>();
    public DbSet<ImportError> ImportErrors => Set<ImportError>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AnomalyResult> AnomalyResults => Set<AnomalyResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Pipeline>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.RouteGeoJson).HasColumnType("longtext");
        });

        modelBuilder.Entity<IliRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.PipelineId).HasMaxLength(36);
            e.Property(x => x.Error).HasColumnType("longtext");
            e.HasOne(x => x.Pipeline).WithMany(x => x.Runs)
                .HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PipelineId, x.InspectionDate });
        });

        modelBuilder.Entity<Anomaly>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasMaxLength(36);
            e.HasOne(x => x.Run).WithMany(x => x.Anomalies)
                .HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RunId, x.DistanceM });
        });

        modelBuilder.Entity<ImportError>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasMaxLength(36);
            e.Property(x => x.Message).HasColumnType("longtext");
            e.HasOne(x => x.Run).WithMany(x => x.ImportErrors)
                .HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Assessment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(36);
            e.Property(x => x.RunId).HasMaxLength(36);
            e.HasOne(x => x.Run).WithMany(x => x.Assessments)
                .HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AnomalyResult>(e =>
        {
            e.HasKey(x => new { x.AssessmentId, x.AnomalyId });
            e.Property(x => x.AssessmentId).HasMaxLength(36);

            e.HasOne(x => x.Assessment).WithMany(x => x.Results)
                .HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Anomaly).WithMany(x => x.Results)
                .HasForeignKey(x => x.AnomalyId).OnDelete(DeleteBehavior.Cascade);

            e.HasOne<Anomaly>().WithMany()
                .HasForeignKey(x => x.MatchedAnomalyId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.AssessmentId, x.Erf }).IsDescending(false, true);
        });
    }
}