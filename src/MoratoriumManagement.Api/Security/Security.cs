using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MoratoriumManagement.Api.Api;
using MoratoriumManagement.Api.Domain;

namespace MoratoriumManagement.Api.Security;

public static class Policies
{
    public const string Admin = "MoratoriumAdmin";
    public const string Reader = "MoratoriumReader";
    public const string Smart = "Smart";
}

public class JwtSettings
{
    public string Issuer { get; set; } = "moratorium-management";
    public string Audience { get; set; } = "broker-portal";
    public string SigningKey { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 480;
}

public class IntegrationSettings
{
    public string ApiKey { get; set; } = "";
    public string DisplayName { get; set; } = "Duck Creek Integration";
}

public sealed class TokenService(IOptions<JwtSettings> options, TimeProvider clock)
{
    private readonly JwtSettings _s = options.Value;

    public static SymmetricSecurityKey KeyOf(JwtSettings s) => new(Encoding.UTF8.GetBytes(s.SigningKey));

    public LoginResponse Issue(AppUser user)
    {
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(_s.LifetimeMinutes);
        var token = new JwtSecurityToken(
            _s.Issuer, _s.Audience,
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.DisplayName),
                new Claim(ClaimTypes.Role, user.Role),
            ],
            expires: expires,
            signingCredentials: new SigningCredentials(KeyOf(_s), SecurityAlgorithms.HmacSha256));
        return new LoginResponse
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = expires,
            User = new UserInfo { Email = user.Email, DisplayName = user.DisplayName, Role = user.Role },
        };
    }
}

/// <summary>Authenticates machine callers (Duck Creek) presenting <c>X-Api-Key</c>. They get read + evaluate access only.</summary>
public sealed class ApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IOptions<IntegrationSettings> integration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var supplied))
            return Task.FromResult(AuthenticateResult.NoResult());

        var expected = integration.Value.ApiKey;
        if (string.IsNullOrEmpty(expected) || !CryptographicEquals(supplied.ToString(), expected))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, integration.Value.DisplayName), new Claim(ClaimTypes.Email, integration.Value.DisplayName),
             new Claim(ClaimTypes.Role, Roles.Viewer)], Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Turns 401/403 into the standard UNAUTHORIZED_ACCESS error body (JSON or XML).</summary>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Succeeded)
        {
            await next(context);
            return;
        }
        var status = result.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        await ApiResponses.WriteAsync(context, status, new ErrorResponse { ErrorCode = ErrorCodes.Unauthorized, Message = Msg.Unauthorized });
    }
}

public static class CurrentUser
{
    public static string Name(this ClaimsPrincipal p) =>
        p.FindFirstValue(ClaimTypes.Email) ?? p.Identity?.Name ?? "unknown";
}
