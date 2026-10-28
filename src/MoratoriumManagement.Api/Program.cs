using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using MoratoriumManagement.Api.Api;
using MoratoriumManagement.Api.Data;
using MoratoriumManagement.Api.Domain;
using MoratoriumManagement.Api.Geography;
using MoratoriumManagement.Api.Security;
using MoratoriumManagement.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var jwt = cfg.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
if (jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters. Set it via configuration or environment.");

builder.Services.Configure<JwtSettings>(cfg.GetSection("Jwt"));
builder.Services.Configure<IntegrationSettings>(cfg.GetSection("Integration"));

var dbPath = cfg["Database:Path"];
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = Path.Combine(builder.Environment.ContentRootPath, "storage", "moratorium.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GeographyCatalog>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<MoratoriumService>();
builder.Services.AddScoped<EvaluationService>();
builder.Services.AddHostedService<MoratoriumScheduler>();

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = Policies.Smart;
        o.DefaultChallengeScheme = Policies.Smart;
    })
    .AddPolicyScheme(Policies.Smart, null, o =>
        o.ForwardDefaultSelector = ctx => ctx.Request.Headers.ContainsKey(ApiKeyHandler.Header)
            ? ApiKeyHandler.Scheme : JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = true;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience,
            IssuerSigningKey = TokenService.KeyOf(jwt),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>(ApiKeyHandler.Scheme, null);

// FR-08: Admin may do everything; Viewer (and the integration key) are read-only.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
    .AddPolicy(Policies.Reader, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Viewer));
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

builder.Services.AddControllers(o =>
    {
        o.Filters.Add<ApiExceptionFilter>();
        o.OutputFormatters.Add(new XmlOutputFormatter());
        o.InputFormatters.Add(new XmlInputFormatter());
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
        new ObjectResult(new ErrorResponse
        {
            ErrorCode = ErrorCodes.InvalidRequest,
            Message = "The request body is missing or malformed.",
            ValidationErrors = ctx.ModelState.Where(kv => kv.Value?.Errors.Count > 0)
                .Select(kv => new FieldError { Field = kv.Key, Message = kv.Value!.Errors[0].ErrorMessage.Length > 0 ? kv.Value.Errors[0].ErrorMessage : "Invalid value." })
                .ToList(),
        })
        { StatusCode = 400 });

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Moratorium Management API", Version = "v1" });
    o.CustomSchemaIds(t => t.FullName!.Replace("+", ".").Split('.').TakeLast(2).Aggregate((a, b) => a + b));
});

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services, cfg.GetSection("Seed").Get<SeedSettings>() ?? new SeedSettings());

app.UseExceptionHandler(e => e.Run(ctx =>
    ApiResponses.WriteAsync(ctx, 500, new ErrorResponse { ErrorCode = ErrorCodes.SystemError, Message = Msg.SystemError })));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Moratorium Management API"));

app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", time = DateTime.UtcNow })).AllowAnonymous();
app.MapControllers();
// Unknown /api routes stay 404; everything else is the Angular SPA.
app.MapFallback("/api/{**rest}", () => Results.NotFound(new ErrorResponse { ErrorCode = ErrorCodes.NotFound, Message = "Resource not found." }));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
