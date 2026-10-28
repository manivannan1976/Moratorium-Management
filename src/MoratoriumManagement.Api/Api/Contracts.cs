using System.Text.Json.Serialization;

namespace MoratoriumManagement.Api.Api;

// ---- Message & response matrix (FR-11) -------------------------------------------------------

public static class Msg
{
    // Administration success messages
    public const string Created = "Moratorium created successfully.";
    public const string Updated = "Moratorium updated successfully.";
    public const string Activated = "Moratorium activated successfully.";
    public const string Deactivated = "Moratorium deactivated successfully.";
    public const string GeographyAdded = "Geographic area added successfully.";
    public const string GeographyRemoved = "Geographic area removed successfully.";
    public const string Scheduled = "Moratorium scheduled successfully.";
    public const string ScheduleUpdated = "Moratorium schedule updated successfully.";

    // Administration error messages
    public const string NotFound = "Requested moratorium does not exist.";
    public const string DuplicateName = "A moratorium with the same name already exists.";
    public const string InvalidState = "Invalid state code provided.";
    public const string InvalidCounty = "Invalid county and state combination.";
    public const string InvalidZip = "ZIP code must contain exactly five numeric digits.";
    public const string InvalidActivation = "Activation date/time is invalid.";
    public const string InvalidDeactivation = "Deactivation date/time must be later than activation date/time.";
    public const string Unauthorized = "User is not authorized to perform this action.";
    public const string SystemError = "An unexpected system error occurred. Please try again later.";
    public const string NameRequired = "Moratorium name is required.";
    public const string StateRequiredForCounty = "State is required for County.";
    public const string StateRequiredForZip = "State is required for ZIP Code.";
    public const string GeographyRequired = "At least one state, county or ZIP code is required.";

    // Evaluation messages
    public const string NoMoratorium = "No active Property moratorium applies.";
    public const string NoMoratoriumInstructions = "Continue normal processing.";
    public const string Applicable = "One or more Property locations are subject to an active moratorium.";
    public const string ApplicableMultiple = "One or more Property locations are subject to multiple active moratoriums.";
    public const string ApplicableInstructions = "Evaluate applicable transaction rules.";
    public const string ApplicableMultipleInstructions = "Review all applicable moratoriums and affected locations.";
    public const string MultipleGeographies = "Active Property Moratorium applies based on multiple configured geographic restrictions.";
    public const string Unavailable = "Unable to verify moratorium status";
    public const string UnavailableInstructions = "Transaction must be blocked until status can be verified.";
    public const string InvalidLocationData = "Invalid geographic information provided.";
    public const string EvalInvalidZip = "ZIP must contain exactly five numeric digits.";
    public const string EvalInvalidCounty = "County must be associated with a valid state.";
    public const string LocationRequired = "At least one location is required.";
}

public static class ErrorCodes
{
    public const string NotFound = "MORATORIUM_NOT_FOUND";
    public const string DuplicateName = "DUPLICATE_MORATORIUM_NAME";
    public const string InvalidState = "INVALID_STATE";
    public const string InvalidCounty = "INVALID_COUNTY";
    public const string InvalidZip = "INVALID_ZIP";
    public const string InvalidActivation = "INVALID_ACTIVATION_DATE";
    public const string InvalidDeactivation = "INVALID_DEACTIVATION_DATE";
    public const string Unauthorized = "UNAUTHORIZED_ACCESS";
    public const string SystemError = "SYSTEM_ERROR";
    public const string NameRequired = "MORATORIUM_NAME_REQUIRED";
    public const string GeographyRequired = "GEOGRAPHY_REQUIRED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidLocationData = "INVALID_LOCATION_DATA";
    public const string ApiUnavailable = "MOR_API_UNAVAILABLE";
}

// ---- Shared envelopes -------------------------------------------------------------------------

public class FieldError
{
    public string? LocationId { get; set; }
    public string? Field { get; set; }
    public string? ErrorCode { get; set; }
    public string Message { get; set; } = "";
}

public class ErrorResponse
{
    public string ErrorCode { get; set; } = "";
    public string Message { get; set; } = "";
    public List<FieldError>? ValidationErrors { get; set; }
}

public class ActionResponse<T>
{
    public string Message { get; set; } = "";
    public T? Data { get; set; }
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
}

// ---- Administration ---------------------------------------------------------------------------

/// <summary>One state entry: statewide flag plus counties and ZIP codes within that state.</summary>
public class AreaDto
{
    public string? State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateName { get; set; }
    public bool Statewide { get; set; }
    public List<string>? Counties { get; set; }
    public List<string>? ZipCodes { get; set; }
}

public class MoratoriumRequest
{
    public string? Name { get; set; }
    public string? TimeZoneId { get; set; }
    /// <summary>ISO-8601. With an offset or "Z" it is absolute; otherwise it is wall-clock time in <see cref="TimeZoneId"/>.</summary>
    public string? ActivationDateTime { get; set; }
    public string? DeactivationDateTime { get; set; }
    public bool ActivateImmediately { get; set; }
    public bool SaveAsDraft { get; set; }
    public List<AreaDto>? Areas { get; set; }
}

public class ActivateRequest
{
    public string? TimeZoneId { get; set; }
    /// <summary>Omit to activate immediately.</summary>
    public string? ActivationDateTime { get; set; }
    public string? DeactivationDateTime { get; set; }
}

public class GeographyChangeRequest
{
    public List<AreaDto>? Areas { get; set; }
}

public class MoratoriumSummary
{
    public string MoratoriumId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string TimeZoneId { get; set; } = "";
    public DateTime? ActivationDateTime { get; set; }
    public DateTime? DeactivationDateTime { get; set; }
    public DateTime LastUpdatedDate { get; set; }
    public string LastUpdatedBy { get; set; } = "";
    public int StateCount { get; set; }
    public int CountyCount { get; set; }
    public int ZipCount { get; set; }
    public string DetailPageUrl { get; set; } = "";
}

public class MoratoriumDetail : MoratoriumSummary
{
    public string EventDescription { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedDate { get; set; }
    public List<AreaDto> Areas { get; set; } = [];
}

public class AuditEntryDto
{
    public long Id { get; set; }
    public string MoratoriumId { get; set; } = "";
    public string MoratoriumName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
}

public class CountyRequest
{
    public string? State { get; set; }
    public string? Name { get; set; }
}

// ---- Evaluation -------------------------------------------------------------------------------

public class EvaluationRequest
{
    public string? TransactionDateTime { get; set; }
    public List<EvaluationLocation>? Locations { get; set; }
}

public class EvaluationLocation
{
    public string? LocationId { get; set; }
    public string? State { get; set; }
    public string? County { get; set; }
    public string? ZipCode { get; set; }
}

public class MatchedCriteria
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}

public class MatchedMoratorium
{
    public string MoratoriumId { get; set; } = "";
    public string MoratoriumName { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTime? ActivationDateTime { get; set; }
    public MatchedCriteria MatchedCriteria { get; set; } = new();
    public List<MatchedCriteria> AllMatchedCriteria { get; set; } = [];
    public string Message { get; set; } = "";
    public string DetailPageUrl { get; set; } = "";
}

public class AffectedLocation
{
    public string LocationId { get; set; } = "";
    public List<MatchedMoratorium> Moratoriums { get; set; } = [];
}

public class EvaluationResponse
{
    public string EvaluationStatus { get; set; } = "SUCCESS";
    /// <summary>Serialized as null (not omitted) on fail-closed responses.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? MoratoriumApplicable { get; set; }
    public string DisplayMessageType { get; set; } = "INFORMATION";
    public string? ErrorCode { get; set; }
    public bool? RetryAllowed { get; set; }
    public string Message { get; set; } = "";
    public string? Instructions { get; set; }
    public string? PortalMoratoriumPageUrl { get; set; }
    public List<AffectedLocation>? AffectedLocations { get; set; }
    public List<FieldError>? ValidationErrors { get; set; }
}

// ---- Auth -------------------------------------------------------------------------------------

public class LoginRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

public class UserInfo
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
}

public class LoginResponse
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public UserInfo User { get; set; } = new();
}

public class ApiException(int status, string code, string message, List<FieldError>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public List<FieldError>? Errors { get; } = errors;
}
