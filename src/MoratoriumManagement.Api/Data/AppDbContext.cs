using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MoratoriumManagement.Api.Domain;

namespace MoratoriumManagement.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Moratorium> Moratoriums => Set<Moratorium>();
    public DbSet<MoratoriumGeography> Geographies => Set<MoratoriumGeography>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefState> RefStates => Set<RefState>();
    public DbSet<RefCounty> RefCounties => Set<RefCounty>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite drops DateTime.Kind; always hand back UTC.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Moratorium>(e =>
        {
            e.HasIndex(x => x.MoratoriumId).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasMany(x => x.Geographies).WithOne().HasForeignKey(g => g.MoratoriumKey).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MoratoriumGeography>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => new { x.MoratoriumKey, x.Type, x.State, x.County, x.Zip }).IsUnique();
            e.HasIndex(x => new { x.Type, x.State, x.Zip });
        });
        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.MoratoriumId);
            e.HasIndex(x => x.TimestampUtc);
        });
        b.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<RefState>().HasKey(x => x.Code);
        b.Entity<RefCounty>().HasIndex(x => new { x.State, x.Name }).IsUnique();
    }
}

internal sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
    v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? v.Value.ToUniversalTime() : v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
