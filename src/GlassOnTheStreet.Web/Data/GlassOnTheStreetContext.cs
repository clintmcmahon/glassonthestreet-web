using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Data;

public class GlassOnTheStreetContext(DbContextOptions<GlassOnTheStreetContext> options) : DbContext(options)
{
    public DbSet<Report> Reports => Set<Report>();

    public DbSet<ReportFlag> ReportFlags => Set<ReportFlag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Report>(entity =>
        {
            entity.Property(r => r.DisplayLat).HasColumnType("decimal(9,6)");
            entity.Property(r => r.DisplayLng).HasColumnType("decimal(9,6)");
            entity.HasIndex(r => r.ReportedDate);
            entity.HasIndex(r => r.Status);
            entity.HasIndex(r => r.ExternalCaseNumber).IsUnique().HasFilter("`ExternalCaseNumber` IS NOT NULL");
        });

        modelBuilder.Entity<ReportFlag>(entity =>
        {
            entity.HasOne(f => f.Report)
                .WithMany(r => r.Flags)
                .HasForeignKey(f => f.ReportId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
