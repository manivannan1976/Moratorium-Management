namespace MoratoriumManagement.Api.Domain;

public enum MoratoriumStatus { Draft, Scheduled, Active, Inactive }

public enum GeographyType { State, County, Zip }

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
}

public class Moratorium
{
    public int Id { get; set; }
    /// <summary>Public identifier, e.g. MOR-1001.</summary>
    public string MoratoriumId { get; set; } = "";
    public string Name { get; set; } = "";
    public string EventDescription { get; set; } = "";
    public MoratoriumStatus Status { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public DateTime? ActivationUtc { get; set; }
    public DateTime? DeactivationUtc { get; set; }
    /// <summary>Set when a user manually lifts the moratorium.</summary>
    public DateTime? LiftedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string LastUpdatedBy { get; set; } = "";
    public DateTime LastUpdatedUtc { get; set; }
    public List<MoratoriumGeography> Geographies { get; set; } = [];
}

public class MoratoriumGeography
{
    public int Id { get; set; }
    public int MoratoriumKey { get; set; }
    public GeographyType Type { get; set; }
    /// <summary>Two-character state code.</summary>
    public string State { get; set; } = "";
    /// <summary>Canonical county name (County rows only).</summary>
    public string? County { get; set; }
    /// <summary>Five-digit ZIP, leading zeros preserved (Zip rows only).</summary>
    public string? Zip { get; set; }
}

/// <summary>Append-only. SQLite triggers reject UPDATE and DELETE.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public string MoratoriumId { get; set; } = "";
    public string MoratoriumName { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
}

public class RefState
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public class RefCounty
{
    public int Id { get; set; }
    public string State { get; set; } = "";
    /// <summary>Official name, e.g. "Miami-Dade County".</summary>
    public string Name { get; set; } = "";
    /// <summary>Canonical name used in moratoriums, e.g. "Miami-Dade".</summary>
    public string ShortName { get; set; } = "";
}

public static class MoratoriumRules
{
    /// <summary>Status implied by the schedule at <paramref name="now"/>. Drafts and lifted moratoriums are not schedule-driven.</summary>
    public static MoratoriumStatus Effective(Moratorium m, DateTime now)
    {
        if (m.Status == MoratoriumStatus.Draft) return MoratoriumStatus.Draft;
        if (m.LiftedUtc is not null) return MoratoriumStatus.Inactive;
        if (m.ActivationUtc is null || m.ActivationUtc > now) return MoratoriumStatus.Scheduled;
        if (m.DeactivationUtc is not null && m.DeactivationUtc <= now) return MoratoriumStatus.Inactive;
        return MoratoriumStatus.Active;
    }
}
