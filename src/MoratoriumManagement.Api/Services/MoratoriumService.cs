using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MoratoriumManagement.Api.Api;
using MoratoriumManagement.Api.Data;
using MoratoriumManagement.Api.Domain;
using MoratoriumManagement.Api.Geography;

namespace MoratoriumManagement.Api.Services;

public sealed record GeoKey(GeographyType Type, string State, string? Value)
{
    public string Label => Type switch
    {
        GeographyType.State => $"State {State}",
        GeographyType.County => $"County {Value}, {State}",
        _ => $"ZIP {Value} ({State})",
    };
}

public sealed record ChangeResult(MoratoriumDetail Moratorium, string Message);

public sealed partial class MoratoriumService(AppDbContext db, GeographyCatalog catalog, TimeProvider clock)
{
    public const string SystemUser = "System";
    private static readonly TimeSpan PastTolerance = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"^\d{5}$")] private static partial Regex ZipPattern();

    public DateTime Now()
    {
        var n = clock.GetUtcNow().UtcDateTime;
        return new DateTime(n.Ticks - n.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    // ---- Queries -------------------------------------------------------------------------------

    public async Task<PagedResult<MoratoriumSummary>> ListAsync(string? status, string? search, int skip, int take, CancellationToken ct)
    {
        await SyncStatusesAsync(ct);
        var q = db.Moratoriums.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<MoratoriumStatus>(status, true, out var s))
                throw new ApiException(400, ErrorCodes.InvalidRequest, $"Unknown status '{status}'.");
            q = q.Where(m => m.Status == s);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(m => EF.Functions.Like(m.Name, $"%{term}%") || EF.Functions.Like(m.MoratoriumId, $"%{term}%"));
        }
        var total = await q.CountAsync(ct);
        // Scheduled moratoriums read best soonest-first; everything else most-recently-changed first.
        var ordered = status?.Equals("Scheduled", StringComparison.OrdinalIgnoreCase) == true
            ? q.OrderBy(m => m.ActivationUtc)
            : q.OrderByDescending(m => m.LastUpdatedUtc);
        var rows = await ordered
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500))
            .Select(m => new
            {
                m,
                States = m.Geographies.Count(g => g.Type == GeographyType.State),
                Counties = m.Geographies.Count(g => g.Type == GeographyType.County),
                Zips = m.Geographies.Count(g => g.Type == GeographyType.Zip),
            })
            .ToListAsync(ct);
        return new PagedResult<MoratoriumSummary>
        {
            Total = total,
            Items = rows.Select(r => ToSummary(new MoratoriumSummary(), r.m, r.States, r.Counties, r.Zips)).ToList(),
        };
    }

    public async Task<MoratoriumDetail> GetAsync(string moratoriumId, CancellationToken ct)
    {
        await SyncStatusesAsync(ct);
        var m = await db.Moratoriums.AsNoTracking().Include(x => x.Geographies)
            .FirstOrDefaultAsync(x => x.MoratoriumId == moratoriumId, ct)
            ?? throw NotFound();
        return ToDetail(m);
    }

    public async Task<PagedResult<AuditEntryDto>> AuditAsync(string? moratoriumId, int skip, int take, CancellationToken ct)
    {
        if (moratoriumId is not null && !await db.Moratoriums.AnyAsync(m => m.MoratoriumId == moratoriumId, ct))
            throw NotFound();
        var q = db.AuditEntries.AsNoTracking().AsQueryable();
        if (moratoriumId is not null) q = q.Where(a => a.MoratoriumId == moratoriumId);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return new PagedResult<AuditEntryDto>
        {
            Total = total,
            Items = rows.Select(a => new AuditEntryDto
            {
                Id = a.Id, MoratoriumId = a.MoratoriumId, MoratoriumName = a.MoratoriumName, Timestamp = a.TimestampUtc,
                User = a.User, Action = a.Action, Detail = a.Detail, PreviousValue = a.OldValue, NewValue = a.NewValue,
            }).ToList(),
        };
    }

    // ---- Commands ------------------------------------------------------------------------------

    public async Task<ChangeResult> CreateAsync(MoratoriumRequest req, string user, CancellationToken ct)
    {
        var now = Now();
        var p = Parse(req, null, now, requireSchedule: !req.SaveAsDraft);
        if (!req.SaveAsDraft && p.Geos.Count == 0 && !p.Errors.Any(e => e.ErrorCode is ErrorCodes.InvalidState or ErrorCodes.InvalidCounty or ErrorCodes.InvalidZip))
            p.Errors.Add(Err(ErrorCodes.GeographyRequired, "areas", Msg.GeographyRequired));
        await CheckDuplicateNameAsync(p, null, ct);
        p.ThrowIfInvalid();

        var m = new Moratorium
        {
            Name = p.Name, TimeZoneId = p.TimeZoneId, ActivationUtc = p.ActivationUtc, DeactivationUtc = p.DeactivationUtc,
            CreatedBy = user, CreatedUtc = now, LastUpdatedBy = user, LastUpdatedUtc = now,
            Status = req.SaveAsDraft ? MoratoriumStatus.Draft : MoratoriumStatus.Scheduled,
            MoratoriumId = "pending-" + Guid.NewGuid().ToString("N"),
        };
        foreach (var g in p.Geos) m.Geographies.Add(ToRow(g));
        m.EventDescription = DescribeEvent(m, p.Geos);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Moratoriums.Add(m);
        await db.SaveChangesAsync(ct);
        m.MoratoriumId = $"MOR-{1000 + m.Id}";
        if (m.Status != MoratoriumStatus.Draft) m.Status = MoratoriumRules.Effective(m, now);

        Audit(m, user, now, "Moratorium Created", null, null, $"{m.Name} ({StatusText(m.Status)})");
        AuditGeography(m, user, now, added: p.Geos, removed: []);
        if (m.Status == MoratoriumStatus.Active)
            Audit(m, user, now, "Moratorium Activated", null, "Draft", "Active");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var message = m.Status == MoratoriumStatus.Scheduled ? Msg.Scheduled : Msg.Created;
        return new ChangeResult(ToDetail(m), message);
    }

    public async Task<ChangeResult> UpdateAsync(string moratoriumId, MoratoriumRequest req, string user, CancellationToken ct)
    {
        var m = await LoadTrackedAsync(moratoriumId, ct);
        var now = Now();
        await SyncStatusesAsync(ct, now);
        var p = Parse(req, m, now, requireSchedule: m.Status != MoratoriumStatus.Draft);
        if (m.Status != MoratoriumStatus.Draft && p.Geos.Count == 0 && p.Errors.Count == 0)
            p.Errors.Add(Err(ErrorCodes.GeographyRequired, "areas", Msg.GeographyRequired));
        await CheckDuplicateNameAsync(p, m.MoratoriumId, ct);
        p.ThrowIfInvalid();

        var before = m.Status;
        var changes = new List<(string Field, string? Old, string? New)>();
        if (m.Name != p.Name) changes.Add(("Name", m.Name, p.Name));
        if (m.ActivationUtc != p.ActivationUtc) changes.Add(("Activation date/time", Fmt(m.ActivationUtc), Fmt(p.ActivationUtc)));
        if (m.DeactivationUtc != p.DeactivationUtc) changes.Add(("Deactivation date/time", Fmt(m.DeactivationUtc), Fmt(p.DeactivationUtc)));
        if (m.TimeZoneId != p.TimeZoneId) changes.Add(("Time zone", m.TimeZoneId, p.TimeZoneId));

        var current = m.Geographies.Select(ToKey).ToHashSet();
        var added = p.Geos.Except(current).ToList();
        var removed = current.Except(p.Geos).ToList();
        foreach (var r in m.Geographies.Where(g => removed.Contains(ToKey(g))).ToList())
        {
            m.Geographies.Remove(r);
            db.Geographies.Remove(r);
        }
        foreach (var a in added) m.Geographies.Add(ToRow(a));

        m.Name = p.Name; m.TimeZoneId = p.TimeZoneId; m.ActivationUtc = p.ActivationUtc; m.DeactivationUtc = p.DeactivationUtc;
        if (m.Status != MoratoriumStatus.Draft)
        {
            // Editing the schedule of a lifted moratorium does not silently re-activate it.
            m.Status = MoratoriumRules.Effective(m, now);
        }
        m.EventDescription = DescribeEvent(m, p.Geos);
        m.LastUpdatedBy = user; m.LastUpdatedUtc = now;

        foreach (var c in changes) Audit(m, user, now, "Moratorium Updated", c.Field, c.Old, c.New);
        AuditGeography(m, user, now, added, removed);
        if (before != m.Status)
        {
            Audit(m, user, now, m.Status == MoratoriumStatus.Active ? "Moratorium Activated" : "Moratorium Deactivated", "Schedule change", before.ToString(), m.Status.ToString());
        }
        await db.SaveChangesAsync(ct);

        var scheduleOnly = changes.Count > 0 && changes.All(c => c.Field.Contains("date/time")) && added.Count == 0 && removed.Count == 0;
        return new ChangeResult(ToDetail(m), scheduleOnly ? Msg.ScheduleUpdated : Msg.Updated);
    }

    public async Task<ChangeResult> ActivateAsync(string moratoriumId, ActivateRequest? req, string user, CancellationToken ct)
    {
        var m = await LoadTrackedAsync(moratoriumId, ct);
        var now = Now();
        await SyncStatusesAsync(ct, now);
        if (m.Status is MoratoriumStatus.Active or MoratoriumStatus.Scheduled && req?.ActivationDateTime is null)
            return new ChangeResult(ToDetail(m), Msg.Activated);

        var errors = new List<FieldError>();
        var tzId = ResolveTimeZone(req?.TimeZoneId ?? m.TimeZoneId, errors);
        var act = now;
        if (req?.ActivationDateTime is not null)
        {
            act = ParseInstant(req.ActivationDateTime, tzId, "activationDateTime", ErrorCodes.InvalidActivation, Msg.InvalidActivation, errors) ?? now;
            if (errors.Count == 0 && act < now - PastTolerance)
                errors.Add(Err(ErrorCodes.InvalidActivation, "activationDateTime", Msg.InvalidActivation));
        }
        var deact = req?.DeactivationDateTime is not null
            ? ParseInstant(req.DeactivationDateTime, tzId, "deactivationDateTime", ErrorCodes.InvalidDeactivation, Msg.InvalidDeactivation, errors)
            : m.DeactivationUtc is { } d && d > act ? d : null;
        if (deact is not null && deact <= act)
            errors.Add(Err(ErrorCodes.InvalidDeactivation, "deactivationDateTime", Msg.InvalidDeactivation));

        var geos = m.Geographies.Select(ToKey).ToHashSet();
        if (geos.Count == 0) errors.Add(Err(ErrorCodes.GeographyRequired, "areas", Msg.GeographyRequired));
        var probe = new Parsed { Name = m.Name, Errors = errors };
        await CheckDuplicateNameAsync(probe, m.MoratoriumId, ct);
        probe.ThrowIfInvalid();

        var before = m.Status;
        m.TimeZoneId = tzId; m.ActivationUtc = act; m.DeactivationUtc = deact; m.LiftedUtc = null;
        m.Status = MoratoriumStatus.Scheduled;
        m.Status = MoratoriumRules.Effective(m, now);
        m.EventDescription = DescribeEvent(m, geos);
        m.LastUpdatedBy = user; m.LastUpdatedUtc = now;
        Audit(m, user, now, "Moratorium Activated", m.Status == MoratoriumStatus.Scheduled ? $"Scheduled for {Fmt(act)}" : null, before.ToString(), m.Status.ToString());
        await db.SaveChangesAsync(ct);
        return new ChangeResult(ToDetail(m), m.Status == MoratoriumStatus.Scheduled ? Msg.Scheduled : Msg.Activated);
    }

    public async Task<ChangeResult> DeactivateAsync(string moratoriumId, string user, CancellationToken ct)
    {
        var m = await LoadTrackedAsync(moratoriumId, ct);
        var now = Now();
        await SyncStatusesAsync(ct, now);
        if (m.Status is MoratoriumStatus.Inactive or MoratoriumStatus.Draft)
            return new ChangeResult(ToDetail(m), Msg.Deactivated);

        var before = m.Status;
        m.LiftedUtc = now;
        m.DeactivationUtc = now;
        m.Status = MoratoriumStatus.Inactive;
        m.EventDescription = DescribeEvent(m, m.Geographies.Select(ToKey).ToHashSet());
        m.LastUpdatedBy = user; m.LastUpdatedUtc = now;
        Audit(m, user, now, "Moratorium Deactivated", "Lifted by user", before.ToString(), "Inactive");
        await db.SaveChangesAsync(ct);
        return new ChangeResult(ToDetail(m), Msg.Deactivated);
    }

    public async Task<ChangeResult> AddGeographiesAsync(string moratoriumId, GeographyChangeRequest req, string user, CancellationToken ct)
    {
        var m = await LoadTrackedAsync(moratoriumId, ct);
        var errors = new List<FieldError>();
        var incoming = ParseAreas(req.Areas, errors);
        if (incoming.Count == 0 && errors.Count == 0) errors.Add(Err(ErrorCodes.GeographyRequired, "areas", Msg.GeographyRequired));
        Throw(errors);

        var now = Now();
        var current = m.Geographies.Select(ToKey).ToHashSet();
        var added = incoming.Except(current).ToList();
        foreach (var a in added) m.Geographies.Add(ToRow(a));
        if (added.Count > 0)
        {
            m.EventDescription = DescribeEvent(m, current.Concat(added).ToHashSet());
            m.LastUpdatedBy = user; m.LastUpdatedUtc = now;
            AuditGeography(m, user, now, added, []);
            await db.SaveChangesAsync(ct);
        }
        return new ChangeResult(ToDetail(m), Msg.GeographyAdded);
    }

    public async Task<ChangeResult> RemoveGeographiesAsync(string moratoriumId, GeographyChangeRequest req, string user, CancellationToken ct)
    {
        var m = await LoadTrackedAsync(moratoriumId, ct);
        var errors = new List<FieldError>();
        var outgoing = ParseAreas(req.Areas, errors);
        Throw(errors);

        var now = Now();
        var rows = m.Geographies.Where(g => outgoing.Contains(ToKey(g))).ToList();
        var remaining = m.Geographies.Except(rows).Select(ToKey).ToHashSet();
        if (rows.Count > 0 && remaining.Count == 0 && m.Status != MoratoriumStatus.Draft)
            throw new ApiException(400, ErrorCodes.GeographyRequired, Msg.GeographyRequired);
        foreach (var r in rows)
        {
            m.Geographies.Remove(r);
            db.Geographies.Remove(r);
        }
        if (rows.Count > 0)
        {
            m.EventDescription = DescribeEvent(m, remaining);
            m.LastUpdatedBy = user; m.LastUpdatedUtc = now;
            AuditGeography(m, user, now, [], rows.Select(ToKey).ToList());
            await db.SaveChangesAsync(ct);
        }
        return new ChangeResult(ToDetail(m), Msg.GeographyRemoved);
    }

    /// <summary>Applies schedule-driven transitions (Scheduled→Active, Active→Inactive) and audits them as System.</summary>
    public async Task<int> SyncStatusesAsync(CancellationToken ct, DateTime? at = null)
    {
        var now = at ?? Now();
        var due = await db.Moratoriums
            .Where(m => (m.Status == MoratoriumStatus.Scheduled && m.ActivationUtc <= now)
                     || (m.Status == MoratoriumStatus.Active && m.DeactivationUtc != null && m.DeactivationUtc <= now))
            .ToListAsync(ct);
        foreach (var m in due)
        {
            var before = m.Status;
            var after = MoratoriumRules.Effective(m, now);
            if (after == before) continue;
            m.Status = after;
            m.LastUpdatedBy = SystemUser; m.LastUpdatedUtc = now;
            Audit(m, SystemUser, now, after == MoratoriumStatus.Active ? "Moratorium Activated" : "Moratorium Deactivated",
                after == MoratoriumStatus.Active ? "Scheduled activation reached" : "Scheduled deactivation reached",
                before.ToString(), after.ToString());
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return due.Count;
    }

    // ---- County maintenance --------------------------------------------------------------------

    public async Task AddCountyAsync(CountyRequest req, CancellationToken ct)
    {
        var state = catalog.NormalizeState(req.State)
            ?? throw new ApiException(400, ErrorCodes.InvalidState, Msg.InvalidState);
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new ApiException(400, ErrorCodes.InvalidCounty, Msg.InvalidCounty);
        if (catalog.ResolveCounty(state, name) is not null)
            throw new ApiException(409, ErrorCodes.InvalidCounty, "County already exists for this state.");
        var official = Regex.IsMatch(name, @"(County|Parish|Borough|Area|Municipality|city)$", RegexOptions.IgnoreCase) ? name : name + " County";
        db.RefCounties.Add(new RefCounty { State = state, Name = official, ShortName = GeographyCatalog.ShortNameOf(official) });
        await db.SaveChangesAsync(ct);
        catalog.Load(await db.RefStates.AsNoTracking().ToListAsync(ct), await db.RefCounties.AsNoTracking().ToListAsync(ct));
    }

    // ---- Parsing & validation ------------------------------------------------------------------

    private sealed class Parsed
    {
        public string Name = "";
        public string TimeZoneId = "UTC";
        public DateTime? ActivationUtc;
        public DateTime? DeactivationUtc;
        public HashSet<GeoKey> Geos = [];
        public List<FieldError> Errors = [];

        public void ThrowIfInvalid() => Throw(Errors);
    }

    private static void Throw(List<FieldError> errors)
    {
        if (errors.Count == 0) return;
        var first = errors[0];
        var status = first.ErrorCode == ErrorCodes.DuplicateName ? 409 : 400;
        throw new ApiException(status, first.ErrorCode!, first.Message, errors);
    }

    private static FieldError Err(string code, string field, string message) => new() { ErrorCode = code, Field = field, Message = message };

    private Parsed Parse(MoratoriumRequest req, Moratorium? existing, DateTime now, bool requireSchedule)
    {
        var p = new Parsed { Name = (req.Name ?? "").Trim() };
        if (p.Name.Length == 0) p.Errors.Add(Err(ErrorCodes.NameRequired, "name", Msg.NameRequired));
        else if (p.Name.Length > 200) p.Errors.Add(Err(ErrorCodes.NameRequired, "name", "Moratorium name must be 200 characters or fewer."));

        p.TimeZoneId = ResolveTimeZone(req.TimeZoneId ?? existing?.TimeZoneId, p.Errors);

        if (req.ActivateImmediately)
        {
            p.ActivationUtc = existing?.ActivationUtc is { } a && existing.Status is MoratoriumStatus.Active ? a : now;
        }
        else if (!string.IsNullOrWhiteSpace(req.ActivationDateTime))
        {
            p.ActivationUtc = KeepIfSameMinute(existing?.ActivationUtc,
                ParseInstant(req.ActivationDateTime, p.TimeZoneId, "activationDateTime", ErrorCodes.InvalidActivation, Msg.InvalidActivation, p.Errors));
            var changed = existing is null || existing.ActivationUtc != p.ActivationUtc;
            var inPast = p.ActivationUtc < now - PastTolerance;
            // Moratoriums that have already started may keep or correct their historical start.
            var mayBePast = existing is { Status: MoratoriumStatus.Active or MoratoriumStatus.Inactive };
            if (changed && inPast && !mayBePast)
                p.Errors.Add(Err(ErrorCodes.InvalidActivation, "activationDateTime", Msg.InvalidActivation));
        }
        else if (requireSchedule)
        {
            p.Errors.Add(Err(ErrorCodes.InvalidActivation, "activationDateTime", Msg.InvalidActivation));
        }

        if (!string.IsNullOrWhiteSpace(req.DeactivationDateTime))
        {
            p.DeactivationUtc = KeepIfSameMinute(existing?.DeactivationUtc,
                ParseInstant(req.DeactivationDateTime, p.TimeZoneId, "deactivationDateTime", ErrorCodes.InvalidDeactivation, Msg.InvalidDeactivation, p.Errors));
            if (p.DeactivationUtc is not null && p.ActivationUtc is not null && p.DeactivationUtc <= p.ActivationUtc)
                p.Errors.Add(Err(ErrorCodes.InvalidDeactivation, "deactivationDateTime", Msg.InvalidDeactivation));
        }

        p.Geos = ParseAreas(req.Areas, p.Errors);
        return p;
    }

    private string ResolveTimeZone(string? id, List<FieldError> errors)
    {
        id = string.IsNullOrWhiteSpace(id) ? "UTC" : id.Trim();
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return id;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add(Err(ErrorCodes.InvalidActivation, "timeZoneId", $"Time zone '{id}' is not recognised."));
            return "UTC";
        }
    }

    private DateTime? ParseInstant(string? text, string tzId, string field, string code, string message, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            if (Regex.IsMatch(text, @"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase))
            {
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                    return Trunc(dto.UtcDateTime);
            }
            else if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                if (!tz.IsInvalidTime(local)) return Trunc(TimeZoneInfo.ConvertTimeToUtc(local, tz));
            }
        }
        catch (ArgumentException) { }
        errors.Add(Err(code, field, message));
        return null;
    }

    /// <summary>The UI edits at minute precision; an unchanged minute must not register as a schedule change.</summary>
    private static DateTime? KeepIfSameMinute(DateTime? existing, DateTime? submitted) =>
        existing is { } e && submitted is { } s && e.Ticks / TimeSpan.TicksPerMinute == s.Ticks / TimeSpan.TicksPerMinute ? e : submitted;

    private static DateTime Trunc(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private HashSet<GeoKey> ParseAreas(List<AreaDto>? areas, List<FieldError> errors)
    {
        var result = new HashSet<GeoKey>();
        var i = 0;
        foreach (var area in areas ?? [])
        {
            var path = $"areas[{i++}]";
            var counties = Split(area.Counties, @"[,;\r\n]+");
            var zips = Split(area.ZipCodes, @"[,;\s]+");
            var hasState = !string.IsNullOrWhiteSpace(area.State);

            if (!hasState && (counties.Count > 0 || zips.Count > 0 || area.Statewide))
            {
                if (counties.Count > 0) errors.Add(Err(ErrorCodes.InvalidState, $"{path}.state", Msg.StateRequiredForCounty));
                if (zips.Count > 0) errors.Add(Err(ErrorCodes.InvalidState, $"{path}.state", Msg.StateRequiredForZip));
                if (counties.Count == 0 && zips.Count == 0) errors.Add(Err(ErrorCodes.InvalidState, $"{path}.state", Msg.InvalidState));
                continue;
            }
            if (!hasState) continue; // fully blank row

            var state = catalog.NormalizeState(area.State);
            if (state is null)
            {
                errors.Add(Err(ErrorCodes.InvalidState, $"{path}.state", Msg.InvalidState));
                continue;
            }
            if (area.Statewide) result.Add(new GeoKey(GeographyType.State, state, null));
            foreach (var c in counties)
            {
                var canon = catalog.ResolveCounty(state, c);
                if (canon is null) errors.Add(Err(ErrorCodes.InvalidCounty, $"{path}.counties", $"{Msg.InvalidCounty} ({c}, {state})"));
                else result.Add(new GeoKey(GeographyType.County, state, canon));
            }
            foreach (var z in zips)
            {
                if (!ZipPattern().IsMatch(z)) errors.Add(Err(ErrorCodes.InvalidZip, $"{path}.zipCodes", $"{Msg.InvalidZip} ({z})"));
                else result.Add(new GeoKey(GeographyType.Zip, state, z));
            }
            if (!area.Statewide && counties.Count == 0 && zips.Count == 0)
                errors.Add(Err(ErrorCodes.GeographyRequired, path, $"{state}: select Statewide or enter at least one county or ZIP code."));
        }
        return result;
    }

    private static List<string> Split(List<string>? values, string separator) =>
        (values ?? []).SelectMany(v => Regex.Split(v ?? "", separator)).Select(v => v.Trim()).Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private async Task CheckDuplicateNameAsync(Parsed p, string? selfId, CancellationToken ct)
    {
        if (p.Name.Length == 0) return;
        var lower = p.Name.ToLower();
        var dup = await db.Moratoriums.AnyAsync(m =>
            m.MoratoriumId != selfId && m.Name.ToLower() == lower &&
            (m.Status == MoratoriumStatus.Active || m.Status == MoratoriumStatus.Scheduled), ct);
        if (dup) p.Errors.Add(Err(ErrorCodes.DuplicateName, "name", Msg.DuplicateName));
    }

    // ---- Mapping, audit & description ----------------------------------------------------------

    private async Task<Moratorium> LoadTrackedAsync(string moratoriumId, CancellationToken ct) =>
        await db.Moratoriums.Include(x => x.Geographies).FirstOrDefaultAsync(x => x.MoratoriumId == moratoriumId, ct)
        ?? throw NotFound();

    private static ApiException NotFound() => new(404, ErrorCodes.NotFound, Msg.NotFound);

    private static GeoKey ToKey(MoratoriumGeography g) => new(g.Type, g.State, g.Type == GeographyType.County ? g.County : g.Zip);

    private static MoratoriumGeography ToRow(GeoKey k) => new()
    {
        Type = k.Type, State = k.State,
        County = k.Type == GeographyType.County ? k.Value : null,
        Zip = k.Type == GeographyType.Zip ? k.Value : null,
    };

    private static string Fmt(DateTime? d) => d is null ? "(none)" : d.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string StatusText(MoratoriumStatus s) => s.ToString();

    private void Audit(Moratorium m, string user, DateTime at, string action, string? detail, string? oldValue, string? newValue) =>
        db.AuditEntries.Add(new AuditEntry
        {
            MoratoriumId = m.MoratoriumId, MoratoriumName = m.Name, TimestampUtc = at, User = user,
            Action = action, Detail = detail, OldValue = oldValue, NewValue = newValue,
        });

    /// <summary>One audit row per (add/remove, geography type, state) so bulk pastes do not flood the history.</summary>
    private void AuditGeography(Moratorium m, string user, DateTime at, IEnumerable<GeoKey> added, IEnumerable<GeoKey> removed)
    {
        foreach (var (action, set) in new[] { ("Geography Added", added), ("Geography Removed", removed) })
        {
            foreach (var grp in set.GroupBy(k => (k.Type, k.State)).OrderBy(g => g.Key.State).ThenBy(g => g.Key.Type))
            {
                var text = grp.Key.Type switch
                {
                    GeographyType.State => $"State {grp.Key.State} (statewide)",
                    GeographyType.County => $"Counties in {grp.Key.State}: {string.Join(", ", grp.Select(k => k.Value).Order())}",
                    _ => $"ZIP codes in {grp.Key.State}: {string.Join(", ", grp.Select(k => k.Value).Order())}",
                };
                var detail = grp.Key.Type switch { GeographyType.State => "State", GeographyType.County => "County", _ => "ZIP" };
                Audit(m, user, at, action, detail, action == "Geography Removed" ? text : null, action == "Geography Added" ? text : null);
            }
        }
    }

    private string DescribeEvent(Moratorium m, IReadOnlySet<GeoKey> geos)
    {
        var parts = new List<string>();
        var states = geos.Where(g => g.Type == GeographyType.State).Select(g => g.State).Order().ToList();
        var counties = geos.Where(g => g.Type == GeographyType.County).ToList();
        var zips = geos.Where(g => g.Type == GeographyType.Zip).ToList();
        if (states.Count > 0) parts.Add($"statewide in {string.Join(", ", states)}");
        if (counties.Count > 0) parts.Add($"{counties.Count} {(counties.Count == 1 ? "county" : "counties")} ({Sample(counties.Select(c => $"{c.Value}, {c.State}"))})");
        if (zips.Count > 0) parts.Add($"{zips.Count} ZIP {(zips.Count == 1 ? "code" : "codes")} ({Sample(zips.Select(z => z.Value!))})");
        var where = parts.Count > 0 ? string.Join("; ", parts) : "no geography defined yet";
        var window = m.ActivationUtc is null ? "Schedule not set."
            : m.DeactivationUtc is null ? $"Effective {Fmt(m.ActivationUtc)} until lifted."
            : $"Effective {Fmt(m.ActivationUtc)} through {Fmt(m.DeactivationUtc)}.";
        return $"Property moratorium \"{m.Name}\" restricting Property risk locations: {where}. {window}";
    }

    private static string Sample(IEnumerable<string> items)
    {
        var list = items.Order().ToList();
        return list.Count <= 3 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(3))} and {list.Count - 3} more";
    }

    private static MoratoriumSummary ToSummary(MoratoriumSummary s, Moratorium m, int states, int counties, int zips)
    {
        s.MoratoriumId = m.MoratoriumId; s.Name = m.Name; s.Status = m.Status.ToString(); s.TimeZoneId = m.TimeZoneId;
        s.ActivationDateTime = m.ActivationUtc; s.DeactivationDateTime = m.DeactivationUtc;
        s.LastUpdatedDate = m.LastUpdatedUtc; s.LastUpdatedBy = m.LastUpdatedBy;
        s.StateCount = states; s.CountyCount = counties; s.ZipCount = zips;
        s.DetailPageUrl = $"/moratoriums/{m.MoratoriumId}";
        return s;
    }

    private MoratoriumDetail ToDetail(Moratorium m)
    {
        var geos = m.Geographies;
        var d = (MoratoriumDetail)ToSummary(new MoratoriumDetail(), m,
            geos.Count(g => g.Type == GeographyType.State), geos.Count(g => g.Type == GeographyType.County), geos.Count(g => g.Type == GeographyType.Zip));
        d.EventDescription = m.EventDescription; d.CreatedBy = m.CreatedBy; d.CreatedDate = m.CreatedUtc;
        d.Areas = geos.GroupBy(g => g.State).OrderBy(g => g.Key).Select(g => new AreaDto
        {
            State = g.Key,
            StateName = catalog.States.GetValueOrDefault(g.Key),
            Statewide = g.Any(x => x.Type == GeographyType.State),
            Counties = g.Where(x => x.Type == GeographyType.County).Select(x => x.County!).Order().ToList(),
            ZipCodes = g.Where(x => x.Type == GeographyType.Zip).Select(x => x.Zip!).Order().ToList(),
        }).ToList();
        return d;
    }
}
