using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MoratoriumManagement.Api.Api;
using MoratoriumManagement.Api.Data;
using MoratoriumManagement.Api.Domain;
using MoratoriumManagement.Api.Geography;

namespace MoratoriumManagement.Api.Services;

/// <summary>
/// FR-09/FR-10: matches Property locations against moratoriums that are Active right now.
/// Drafts, scheduled (not yet started), lifted and expired moratoriums never match.
/// </summary>
public sealed class EvaluationService(AppDbContext db, GeographyCatalog catalog, TimeProvider clock)
{
    private const string PortalUrl = "/moratoriums";

    public async Task<EvaluationResponse> EvaluateAsync(EvaluationRequest? req, CancellationToken ct)
    {
        var validation = Validate(req, out var locations);
        if (validation.Count > 0)
        {
            return new EvaluationResponse
            {
                EvaluationStatus = "FAILED",
                DisplayMessageType = "ERROR",
                ErrorCode = ErrorCodes.InvalidLocationData,
                Message = Msg.InvalidLocationData,
                ValidationErrors = validation,
            };
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var active = await db.Moratoriums.AsNoTracking().Include(m => m.Geographies)
            .Where(m => m.Status != MoratoriumStatus.Draft && m.LiftedUtc == null
                     && m.ActivationUtc != null && m.ActivationUtc <= now
                     && (m.DeactivationUtc == null || m.DeactivationUtc > now))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        var index = active.Select(m => new
        {
            M = m,
            States = m.Geographies.Where(g => g.Type == GeographyType.State).Select(g => g.State).ToHashSet(),
            Counties = m.Geographies.Where(g => g.Type == GeographyType.County).Select(g => (g.State, g.County!)).ToHashSet(),
            Zips = m.Geographies.Where(g => g.Type == GeographyType.Zip).Select(g => g.Zip!).ToHashSet(),
        }).ToList();

        var affected = new List<AffectedLocation>();
        foreach (var loc in locations)
        {
            var hits = new List<MatchedMoratorium>();
            foreach (var x in index)
            {
                var criteria = new List<MatchedCriteria>();
                if (x.Zips.Contains(loc.Zip)) criteria.Add(new() { Type = "ZIP", Value = loc.Zip });
                if (loc.County is not null && x.Counties.Contains((loc.State, loc.County)))
                    criteria.Add(new() { Type = "COUNTY", Value = $"{loc.County}, {loc.State}" });
                if (x.States.Contains(loc.State)) criteria.Add(new() { Type = "STATE", Value = loc.State });
                if (criteria.Count == 0) continue;

                hits.Add(new MatchedMoratorium
                {
                    MoratoriumId = x.M.MoratoriumId,
                    MoratoriumName = x.M.Name,
                    ActivationDateTime = x.M.ActivationUtc,
                    MatchedCriteria = criteria[0],
                    AllMatchedCriteria = criteria,
                    Message = MatchMessage(x.M.Name, criteria),
                    DetailPageUrl = $"{PortalUrl}/{x.M.MoratoriumId}",
                });
            }
            if (hits.Count > 0) affected.Add(new AffectedLocation { LocationId = loc.Id, Moratoriums = hits });
        }

        if (affected.Count == 0)
        {
            return new EvaluationResponse
            {
                EvaluationStatus = "SUCCESS", MoratoriumApplicable = false, DisplayMessageType = "INFORMATION",
                Message = Msg.NoMoratorium, Instructions = Msg.NoMoratoriumInstructions, AffectedLocations = [],
            };
        }

        var multiple = affected.Any(a => a.Moratoriums.Count > 1);
        return new EvaluationResponse
        {
            EvaluationStatus = "SUCCESS", MoratoriumApplicable = true, DisplayMessageType = "WARNING",
            Message = multiple ? Msg.ApplicableMultiple : Msg.Applicable,
            Instructions = multiple ? Msg.ApplicableMultipleInstructions : Msg.ApplicableInstructions,
            PortalMoratoriumPageUrl = PortalUrl,
            AffectedLocations = affected,
        };
    }

    public static EvaluationResponse FailClosed() => new()
    {
        EvaluationStatus = "UNAVAILABLE",
        MoratoriumApplicable = null,
        DisplayMessageType = "BLOCKING",
        ErrorCode = ErrorCodes.ApiUnavailable,
        RetryAllowed = true,
        Message = Msg.Unavailable,
        Instructions = Msg.UnavailableInstructions,
    };

    private static string MatchMessage(string name, List<MatchedCriteria> criteria)
    {
        if (criteria.Count > 1) return Msg.MultipleGeographies;
        var c = criteria[0];
        return c.Type switch
        {
            "ZIP" => $"Active Property Moratorium affecting ZIP Code {c.Value}.",
            "COUNTY" => $"Active Property Moratorium affecting {c.Value.Replace(", ", " County, ")}.",
            _ => $"Active Property Moratorium: {name}.",
        };
    }

    private sealed record Loc(string Id, string State, string? County, string Zip);

    private List<FieldError> Validate(EvaluationRequest? req, out List<Loc> locations)
    {
        locations = [];
        var errors = new List<FieldError>();
        if (!string.IsNullOrWhiteSpace(req?.TransactionDateTime) &&
            !DateTimeOffset.TryParse(req.TransactionDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add(new() { Field = "transactionDateTime", ErrorCode = ErrorCodes.InvalidRequest, Message = "Transaction date/time is invalid." });
        }
        if (req?.Locations is not { Count: > 0 })
        {
            errors.Add(new() { Field = "locations", ErrorCode = ErrorCodes.InvalidLocationData, Message = Msg.LocationRequired });
            return errors;
        }

        foreach (var (l, i) in req.Locations.Select((l, i) => (l, i)))
        {
            var id = string.IsNullOrWhiteSpace(l?.LocationId) ? $"#{i + 1}" : l!.LocationId!.Trim();
            void Add(string field, string code, string msg) =>
                errors.Add(new() { LocationId = id, Field = field, ErrorCode = code, Message = msg });

            if (l is null) { Add("location", ErrorCodes.InvalidLocationData, Msg.InvalidLocationData); continue; }
            if (string.IsNullOrWhiteSpace(l.LocationId)) Add("locationId", ErrorCodes.InvalidLocationData, "Location ID is required.");

            var state = catalog.NormalizeState(l.State);
            if (state is null) Add("state", ErrorCodes.InvalidState, Msg.InvalidState);

            var zip = l.ZipCode?.Trim() ?? "";
            var zipOk = zip.Length == 5 && zip.All(char.IsAsciiDigit);
            if (!zipOk) Add("zipCode", ErrorCodes.InvalidZip, Msg.EvalInvalidZip);

            string? county = null;
            if (!string.IsNullOrWhiteSpace(l.County))
            {
                county = state is null ? null : catalog.ResolveCounty(state, l.County);
                if (county is null) Add("county", ErrorCodes.InvalidCounty, Msg.EvalInvalidCounty);
            }
            if (state is not null && zipOk && errors.All(e => e.LocationId != id))
                locations.Add(new Loc(id, state, county, zip));
        }
        return errors;
    }
}
