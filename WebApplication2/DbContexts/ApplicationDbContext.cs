using Microsoft.EntityFrameworkCore;
using WebApplication2.Entities;

namespace WebApplication2.DbContexts;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Geometry> Geometries => Set<Geometry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Geometry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Data).HasColumnType("TEXT");
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.Error).HasColumnType("TEXT");
        });
    }
}