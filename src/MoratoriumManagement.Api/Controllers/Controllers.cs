using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoratoriumManagement.Api.Api;
using MoratoriumManagement.Api.Data;
using MoratoriumManagement.Api.Domain;
using MoratoriumManagement.Api.Geography;
using MoratoriumManagement.Api.Security;
using MoratoriumManagement.Api.Services;

namespace MoratoriumManagement.Api.Controllers;

[ApiController, Route("api/auth")]
public class AuthController(AppDbContext db, TokenService tokens) : ControllerBase
{
    [HttpPost("login"), AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var ok = user is not null &&
                 new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") != PasswordVerificationResult.Failed;
        if (!ok) throw new ApiException(401, ErrorCodes.Unauthorized, "Invalid email or password.");
        return tokens.Issue(user!);
    }

    [HttpGet("me"), Authorize(Policy = Policies.Reader)]
    public ActionResult<UserInfo> Me() => new UserInfo
    {
        Email = User.Name(),
        DisplayName = User.Identity?.Name ?? "",
        Role = User.IsInRole(Roles.Admin) ? Roles.Admin : Roles.Viewer,
    };
}

/// <summary>Moratorium administration, retrieval and audit APIs.</summary>
[ApiController, Route("api/moratoriums"), Authorize(Policy = Policies.Reader)]
public class MoratoriumsController(MoratoriumService svc) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<MoratoriumSummary>> List(string? status, string? search, int skip = 0, int take = 50, CancellationToken ct = default) =>
        svc.ListAsync(status, search, skip, take, ct);

    /// <summary>Retrieve Active Moratoriums API.</summary>
    [HttpGet("active")]
    public Task<PagedResult<MoratoriumSummary>> Active(int skip = 0, int take = 200, CancellationToken ct = default) =>
        svc.ListAsync("Active", null, skip, take, ct);

    /// <summary>Retrieve Moratorium Details API.</summary>
    [HttpGet("{id}")]
    public Task<MoratoriumDetail> Get(string id, CancellationToken ct) => svc.GetAsync(id, ct);

    /// <summary>Retrieve Audit History API for one moratorium.</summary>
    [HttpGet("{id}/audit")]
    public Task<PagedResult<AuditEntryDto>> Audit(string id, int skip = 0, int take = 100, CancellationToken ct = default) =>
        svc.AuditAsync(id, skip, take, ct);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Create(MoratoriumRequest req, CancellationToken ct)
    {
        var r = await svc.CreateAsync(req, User.Name(), ct);
        return Created($"/api/moratoriums/{r.Moratorium.MoratoriumId}", Wrap(r));
    }

    [HttpPut("{id}"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ActionResponse<MoratoriumDetail>>> Update(string id, MoratoriumRequest req, CancellationToken ct) =>
        Wrap(await svc.UpdateAsync(id, req, User.Name(), ct));

    [HttpPost("{id}/activate"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ActionResponse<MoratoriumDetail>>> Activate(string id, [FromBody] ActivateRequest? req, CancellationToken ct) =>
        Wrap(await svc.ActivateAsync(id, req, User.Name(), ct));

    [HttpPost("{id}/deactivate"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ActionResponse<MoratoriumDetail>>> Deactivate(string id, CancellationToken ct) =>
        Wrap(await svc.DeactivateAsync(id, User.Name(), ct));

    [HttpPost("{id}/geographies"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ActionResponse<MoratoriumDetail>>> AddGeography(string id, GeographyChangeRequest req, CancellationToken ct) =>
        Wrap(await svc.AddGeographiesAsync(id, req, User.Name(), ct));

    [HttpPost("{id}/geographies/remove"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ActionResponse<MoratoriumDetail>>> RemoveGeography(string id, GeographyChangeRequest req, CancellationToken ct) =>
        Wrap(await svc.RemoveGeographiesAsync(id, req, User.Name(), ct));

    private static ActionResponse<MoratoriumDetail> Wrap(ChangeResult r) => new() { Message = r.Message, Data = r.Moratorium };
}

[ApiController, Route("api/audit"), Authorize(Policy = Policies.Reader)]
public class AuditController(MoratoriumService svc) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditEntryDto>> All(string? moratoriumId, int skip = 0, int take = 100, CancellationToken ct = default) =>
        svc.AuditAsync(moratoriumId, skip, take, ct);
}

/// <summary>
/// Moratorium Evaluation API. Always answers with a deterministic body; any internal failure
/// returns the fail-closed UNAVAILABLE response so Duck Creek blocks the transaction.
/// </summary>
[ApiController, Route("api/moratoriums/evaluate"), Authorize(Policy = Policies.Reader)]
public class EvaluationController(EvaluationService evaluation, ILogger<EvaluationController> log) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Evaluate([FromBody] EvaluationRequest? req, CancellationToken ct)
    {
        try
        {
            var result = await evaluation.EvaluateAsync(req, ct);
            return new ObjectResult(result) { StatusCode = result.EvaluationStatus == "FAILED" ? 400 : 200 };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Moratorium evaluation failed; returning fail-closed response");
            return new ObjectResult(EvaluationService.FailClosed()) { StatusCode = 503 };
        }
    }
}

[ApiController, Route("api/reference"), Authorize(Policy = Policies.Reader)]
public class ReferenceController(GeographyCatalog catalog, MoratoriumService svc) : ControllerBase
{
    public class StateDto { public string Code { get; set; } = ""; public string Name { get; set; } = ""; }

    [HttpGet("states")]
    public PagedResult<StateDto> States()
    {
        var items = catalog.States.OrderBy(s => s.Value).Select(s => new StateDto { Code = s.Key, Name = s.Value }).ToList();
        return new PagedResult<StateDto> { Items = items, Total = items.Count };
    }

    [HttpGet("states/{code}/counties")]
    public PagedResult<string> Counties(string code)
    {
        var state = catalog.NormalizeState(code) ?? throw new ApiException(400, ErrorCodes.InvalidState, Msg.InvalidState);
        var items = catalog.Counties(state).ToList();
        return new PagedResult<string> { Items = items, Total = items.Count };
    }

    /// <summary>County maintenance: add a county missing from the reference list.</summary>
    [HttpPost("counties"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> AddCounty(CountyRequest req, CancellationToken ct)
    {
        await svc.AddCountyAsync(req, ct);
        return Ok(new ActionResponse<object> { Message = "County added successfully." });
    }
}
