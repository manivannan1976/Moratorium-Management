using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoratoriumManagement.Api.Api;
using MoratoriumManagement.Api.Domain;
using MoratoriumManagement.Api.Geography;
using MoratoriumManagement.Api.Services;

namespace MoratoriumManagement.Api.Data;

public class SeedSettings
{
    public bool DemoData { get; set; }
    public List<SeedUser> Users { get; set; } = [];
}

public class SeedUser
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
}

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider sp, SeedSettings seed)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<GeographyCatalog>();

        await db.Database.EnsureCreatedAsync();
        // FR-04: historical audit entries cannot be modified, enforced by the database itself.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS AuditEntries_no_update BEFORE UPDATE ON AuditEntries
            BEGIN SELECT RAISE(ABORT, 'Audit entries are immutable.'); END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS AuditEntries_no_delete BEFORE DELETE ON AuditEntries
            BEGIN SELECT RAISE(ABORT, 'Audit entries are immutable.'); END;
            """);

        if (!await db.RefStates.AnyAsync())
        {
            db.RefStates.AddRange(UsStates.Names.Select(kv => new RefState { Code = kv.Key, Name = kv.Value }));
            db.RefCounties.AddRange(GeographyCatalog.ReadSeed());
            await db.SaveChangesAsync();
        }
        catalog.Load(await db.RefStates.AsNoTracking().ToListAsync(), await db.RefCounties.AsNoTracking().ToListAsync());

        if (!await db.Users.AnyAsync())
        {
            var hasher = new PasswordHasher<AppUser>();
            foreach (var u in seed.Users)
            {
                var user = new AppUser { Email = u.Email.ToLowerInvariant(), DisplayName = u.DisplayName, Role = u.Role };
                user.PasswordHash = hasher.HashPassword(user, u.Password);
                db.Users.Add(user);
            }
            await db.SaveChangesAsync();
        }

        if (seed.DemoData && !await db.Moratoriums.AnyAsync())
            await SeedDemoAsync(scope.ServiceProvider.GetRequiredService<MoratoriumService>());
    }

    private static async Task SeedDemoAsync(MoratoriumService svc)
    {
        var u = MoratoriumService.SystemUser;
        var now = DateTime.UtcNow;
        string Iso(TimeSpan offset) => now.Add(offset).ToString("yyyy-MM-ddTHH:mm:ss'Z'");

        await svc.CreateAsync(new MoratoriumRequest
        {
            Name = "Florida State Moratorium", TimeZoneId = "America/New_York", ActivateImmediately = true,
            Areas = [new AreaDto { State = "FL", Statewide = true }],
        }, u, default);
        await svc.CreateAsync(new MoratoriumRequest
        {
            Name = "Hurricane Milton", TimeZoneId = "America/New_York", ActivateImmediately = true,
            Areas =
            [
                new AreaDto { State = "FL", Counties = ["Miami-Dade", "Broward", "Hillsborough"], ZipCodes = ["33101", "33102", "33139", "33602"] },
                new AreaDto { State = "GA", Counties = ["Chatham"], ZipCodes = ["31401"] },
            ],
        }, u, default);
        await svc.CreateAsync(new MoratoriumRequest
        {
            Name = "California Wildfire Response", TimeZoneId = "America/Los_Angeles", ActivationDateTime = Iso(TimeSpan.FromDays(7)),
            DeactivationDateTime = Iso(TimeSpan.FromDays(60)),
            Areas = [new AreaDto { State = "CA", Counties = ["Los Angeles", "Ventura"], ZipCodes = ["90210", "93001"] }],
        }, u, default);
        await svc.CreateAsync(new MoratoriumRequest
        {
            Name = "Texas Winter Storm (Draft)", TimeZoneId = "America/Chicago", SaveAsDraft = true,
            Areas = [new AreaDto { State = "TX", Counties = ["Travis", "Harris"] }],
        }, u, default);
        var lifted = await svc.CreateAsync(new MoratoriumRequest
        {
            Name = "Carolina Tropical Storm", TimeZoneId = "America/New_York", ActivateImmediately = true,
            Areas = [new AreaDto { State = "SC", Counties = ["Charleston", "Horry"] }],
        }, u, default);
        await svc.DeactivateAsync(lifted.Moratorium.MoratoriumId, u, default);
    }
}
