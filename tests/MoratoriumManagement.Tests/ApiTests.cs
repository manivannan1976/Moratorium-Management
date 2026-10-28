using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace MoratoriumManagement.Tests;

/// <summary>One isolated API + SQLite file per test class.</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"mm-test-{Guid.NewGuid():N}.db");

    public ApiFixture()
    {
        // Read by WebApplication.CreateBuilder before any test hook could run.
        Environment.SetEnvironmentVariable("Database__Path", DbPath);
        Environment.SetEnvironmentVariable("Seed__DemoData", "false");
        Environment.SetEnvironmentVariable("Scheduler__IntervalSeconds", "3600");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(DbPath); } catch (IOException) { }
    }
}

public class ApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string ApiKey = "duckcreek-local-dev-key";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> ClientAsync(string email = "admin@portal.local", string password = "Admin@12345")
    {
        var c = api.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private HttpClient Integration()
    {
        var c = api.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return c;
    }

    private static object Area(string state, bool statewide = false, string[]? counties = null, string[]? zips = null) =>
        new { state, statewide, counties = counties ?? [], zipCodes = zips ?? [] };

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..28];

    private async Task<JsonNode> CreateAsync(HttpClient c, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var res = await c.PostAsJsonAsync("/api/moratoriums", body);
        var json = (await res.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.True(res.StatusCode == expected, $"{res.StatusCode}: {json}");
        return json;
    }

    private async Task<JsonNode> EvaluateAsync(string state, string zip, string? county = null)
    {
        var res = await Integration().PostAsJsonAsync("/api/moratoriums/evaluate", new
        {
            transactionDateTime = "2027-01-08T08:00:00Z",
            locations = new[] { new { locationId = "LOC001", state, county, zipCode = zip } },
        });
        return (await res.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    // ---- Security (FR-08, BR-01) ----

    [Fact]
    public async Task Anonymous_requests_are_rejected_with_standard_error()
    {
        var res = await api.CreateClient().GetAsync("/api/moratoriums");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("UNAUTHORIZED_ACCESS", body["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Viewer_can_read_but_not_change()
    {
        var viewer = await ClientAsync("viewer@portal.local", "Viewer@12345");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/moratoriums")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/audit")).StatusCode);

        var create = await viewer.PostAsJsonAsync("/api/moratoriums", new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/moratoriums/MOR-1001/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Bad_credentials_and_bad_api_key_are_rejected()
    {
        var login = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "admin@portal.local", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        var c = api.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/moratoriums/active")).StatusCode);
    }

    [Fact]
    public async Task Integration_key_cannot_administer()
    {
        var res = await Integration().PostAsJsonAsync("/api/moratoriums", new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Lifecycle & evaluation (FR-01, FR-05, FR-09, FR-10) ----

    [Fact]
    public async Task Active_moratorium_matches_by_state_county_and_zip_and_returns_all_matches()
    {
        var admin = await ClientAsync();
        var state = await CreateAsync(admin, new { name = Unique("State"), timeZoneId = "America/New_York", activateImmediately = true, areas = new[] { Area("NV", statewide: true) } });
        Assert.Equal("Moratorium created successfully.", state["message"]!.GetValue<string>());
        var zip = await CreateAsync(admin, new { name = Unique("Zip"), activateImmediately = true, areas = new[] { Area("NV", zips: ["89101"]) } });
        var county = await CreateAsync(admin, new { name = Unique("County"), activateImmediately = true, areas = new[] { Area("NV", counties: ["Clark County"]) } });

        var stateId = state["data"]!["moratoriumId"]!.GetValue<string>();
        var zipId = zip["data"]!["moratoriumId"]!.GetValue<string>();
        var countyId = county["data"]!["moratoriumId"]!.GetValue<string>();

        var r = await EvaluateAsync("NV", "89101", "Clark");
        Assert.True(r["moratoriumApplicable"]!.GetValue<bool>());
        Assert.Equal("WARNING", r["displayMessageType"]!.GetValue<string>());
        Assert.Equal("/moratoriums", r["portalMoratoriumPageUrl"]!.GetValue<string>());
        var hits = r["affectedLocations"]![0]!["moratoriums"]!.AsArray();
        Assert.Equal(3, hits.Count);
        var byId = hits.ToDictionary(h => h!["moratoriumId"]!.GetValue<string>(), h => h!);
        Assert.Equal("STATE", byId[stateId]["matchedCriteria"]!["type"]!.GetValue<string>());
        Assert.Equal("ZIP", byId[zipId]["matchedCriteria"]!["type"]!.GetValue<string>());
        Assert.Equal("Clark, NV", byId[countyId]["matchedCriteria"]!["value"]!.GetValue<string>());
        Assert.Contains("multiple", r["message"]!.GetValue<string>());

        // Different ZIP, same state: only the statewide moratorium applies.
        var only = await EvaluateAsync("NV", "89501");
        Assert.Single(only["affectedLocations"]![0]!["moratoriums"]!.AsArray());
    }

    [Fact]
    public async Task No_match_returns_information_and_empty_list()
    {
        var r = await EvaluateAsync("WY", "82001");
        Assert.False(r["moratoriumApplicable"]!.GetValue<bool>());
        Assert.Equal("INFORMATION", r["displayMessageType"]!.GetValue<string>());
        Assert.Equal("No active Property moratorium applies.", r["message"]!.GetValue<string>());
        Assert.Empty(r["affectedLocations"]!.AsArray());
    }

    [Fact]
    public async Task Scheduled_drafts_and_lifted_moratoriums_are_excluded_from_evaluation()
    {
        var admin = await ClientAsync();
        var future = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-ddTHH:mm:ss'Z'");
        var scheduled = await CreateAsync(admin, new { name = Unique("Sched"), activationDateTime = future, areas = new[] { Area("MT", statewide: true) } });
        Assert.Equal("Scheduled", scheduled["data"]!["status"]!.GetValue<string>());
        Assert.Equal("Moratorium scheduled successfully.", scheduled["message"]!.GetValue<string>());

        await CreateAsync(admin, new { name = Unique("Draft"), saveAsDraft = true, areas = new[] { Area("MT", statewide: true) } });
        Assert.False((await EvaluateAsync("MT", "59601"))["moratoriumApplicable"]!.GetValue<bool>());

        var live = await CreateAsync(admin, new { name = Unique("Live"), activateImmediately = true, areas = new[] { Area("ND", statewide: true) } });
        var id = live["data"]!["moratoriumId"]!.GetValue<string>();
        Assert.True((await EvaluateAsync("ND", "58501"))["moratoriumApplicable"]!.GetValue<bool>());

        var lifted = await admin.PostAsync($"/api/moratoriums/{id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal("Moratorium deactivated successfully.", (await lifted.Content.ReadFromJsonAsync<JsonNode>())!["message"]!.GetValue<string>());
        Assert.False((await EvaluateAsync("ND", "58501"))["moratoriumApplicable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Activating_a_draft_makes_it_apply_immediately()
    {
        var admin = await ClientAsync();
        var d = await CreateAsync(admin, new { name = Unique("Draft2"), saveAsDraft = true, areas = new[] { Area("SD", zips: ["57501"]) } });
        var id = d["data"]!["moratoriumId"]!.GetValue<string>();
        Assert.False((await EvaluateAsync("SD", "57501"))["moratoriumApplicable"]!.GetValue<bool>());

        var res = await admin.PostAsJsonAsync($"/api/moratoriums/{id}/activate", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await EvaluateAsync("SD", "57501"))["moratoriumApplicable"]!.GetValue<bool>());
    }

    // ---- Validation (FR-03) ----

    [Fact]
    public async Task Invalid_geography_cannot_be_saved()
    {
        var admin = await ClientAsync();
        var bad = await CreateAsync(admin, new
        {
            name = Unique("Bad"), activateImmediately = true,
            areas = new[] { Area("ZZ"), Area("FL", counties: ["Nowhere"], zips: ["1234", "ABCDE"]) },
        }, HttpStatusCode.BadRequest);
        var codes = bad["validationErrors"]!.AsArray().Select(e => e!["errorCode"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("INVALID_STATE", codes);
        Assert.Contains("INVALID_COUNTY", codes);
        Assert.Contains("INVALID_ZIP", codes);
    }

    [Fact]
    public async Task County_without_state_is_rejected()
    {
        var admin = await ClientAsync();
        var bad = await CreateAsync(admin, new { name = Unique("NoState"), activateImmediately = true, areas = new object[] { new { counties = new[] { "Dade" } } } }, HttpStatusCode.BadRequest);
        Assert.Equal("State is required for County.", bad["validationErrors"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Leading_zero_zips_and_county_aliases_are_normalised()
    {
        var admin = await ClientAsync();
        var res = await CreateAsync(admin, new
        {
            name = Unique("Norm"), activateImmediately = true,
            areas = new[] { Area("ma", counties: ["suffolk county", "SUFFOLK"], zips: ["02108,02109 02110"]) },
        });
        var area = res["data"]!["areas"]![0]!;
        Assert.Equal("MA", area["state"]!.GetValue<string>());
        Assert.Equal(["Suffolk"], area["counties"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["02108", "02109", "02110"], area["zipCodes"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task Duplicate_name_among_active_and_scheduled_is_rejected_and_deactivation_must_follow_activation()
    {
        var admin = await ClientAsync();
        var name = Unique("Dup");
        await CreateAsync(admin, new { name, activateImmediately = true, areas = new[] { Area("VT", statewide: true) } });
        var dup = await CreateAsync(admin, new { name = name.ToUpperInvariant(), activateImmediately = true, areas = new[] { Area("VT", statewide: true) } }, HttpStatusCode.Conflict);
        Assert.Equal("DUPLICATE_MORATORIUM_NAME", dup["errorCode"]!.GetValue<string>());

        var start = DateTime.UtcNow.AddDays(2);
        var badWindow = await CreateAsync(admin, new
        {
            name = Unique("Window"), activationDateTime = start.ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
            deactivationDateTime = start.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss'Z'"), areas = new[] { Area("VT", statewide: true) },
        }, HttpStatusCode.BadRequest);
        Assert.Equal("INVALID_DEACTIVATION_DATE", badWindow["errorCode"]!.GetValue<string>());

        var past = await CreateAsync(admin, new { name = Unique("Past"), activationDateTime = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-ddTHH:mm:ss'Z'"), areas = new[] { Area("VT", statewide: true) } }, HttpStatusCode.BadRequest);
        Assert.Equal("INVALID_ACTIVATION_DATE", past["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_moratorium_returns_not_found()
    {
        var admin = await ClientAsync();
        var res = await admin.GetAsync("/api/moratoriums/MOR-999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("MORATORIUM_NOT_FOUND", (await res.Content.ReadFromJsonAsync<JsonNode>())!["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Evaluation_rejects_bad_location_data_with_validation_errors()
    {
        var res = await Integration().PostAsJsonAsync("/api/moratoriums/evaluate", new
        {
            locations = new[] { new { locationId = "LOC001", state = "FL", zipCode = "331" }, new { locationId = "LOC002", state = "XX", zipCode = "33101" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("FAILED", body["evaluationStatus"]!.GetValue<string>());
        Assert.Equal("INVALID_LOCATION_DATA", body["errorCode"]!.GetValue<string>());
        var errors = body["validationErrors"]!.AsArray();
        Assert.Contains(errors, e => e!["locationId"]!.GetValue<string>() == "LOC001" && e["field"]!.GetValue<string>() == "zipCode"
            && e["message"]!.GetValue<string>() == "ZIP must contain exactly five numeric digits.");
        Assert.Contains(errors, e => e!["field"]!.GetValue<string>() == "state");
    }

    // ---- Update, geography changes, audit (FR-04) ----

    [Fact]
    public async Task Updates_are_reflected_immediately_and_fully_audited()
    {
        var admin = await ClientAsync();
        var name = Unique("Audit");
        var created = await CreateAsync(admin, new { name, activateImmediately = true, areas = new[] { Area("OR", zips: ["97201"]) } });
        var id = created["data"]!["moratoriumId"]!.GetValue<string>();
        Assert.True((await EvaluateAsync("OR", "97201"))["moratoriumApplicable"]!.GetValue<bool>());
        Assert.False((await EvaluateAsync("OR", "97202"))["moratoriumApplicable"]!.GetValue<bool>());

        var renamed = name + " v2";
        var put = await admin.PutAsJsonAsync($"/api/moratoriums/{id}", new
        {
            name = renamed, activateImmediately = false, activationDateTime = created["data"]!["activationDateTime"]!.GetValue<string>(),
            areas = new[] { Area("OR", zips: ["97202"]) },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await EvaluateAsync("OR", "97201"))["moratoriumApplicable"]!.GetValue<bool>());
        Assert.True((await EvaluateAsync("OR", "97202"))["moratoriumApplicable"]!.GetValue<bool>());

        var add = await admin.PostAsJsonAsync($"/api/moratoriums/{id}/geographies", new { areas = new[] { Area("WA", statewide: true) } });
        Assert.Equal("Geographic area added successfully.", (await add.Content.ReadFromJsonAsync<JsonNode>())!["message"]!.GetValue<string>());
        Assert.True((await EvaluateAsync("WA", "98101"))["moratoriumApplicable"]!.GetValue<bool>());
        var remove = await admin.PostAsJsonAsync($"/api/moratoriums/{id}/geographies/remove", new { areas = new[] { Area("WA", statewide: true) } });
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.False((await EvaluateAsync("WA", "98101"))["moratoriumApplicable"]!.GetValue<bool>());

        var audit = (await admin.GetFromJsonAsync<JsonNode>($"/api/moratoriums/{id}/audit"))!["items"]!.AsArray();
        var actions = audit.Select(a => a!["action"]!.GetValue<string>()).ToList();
        foreach (var expected in new[] { "Moratorium Created", "Moratorium Activated", "Moratorium Updated", "Geography Added", "Geography Removed" })
            Assert.Contains(expected, actions);
        var nameChange = audit.First(a => a!["action"]!.GetValue<string>() == "Moratorium Updated")!;
        Assert.Equal(name, nameChange["previousValue"]!.GetValue<string>());
        Assert.Equal(renamed, nameChange["newValue"]!.GetValue<string>());
        Assert.Equal("admin@portal.local", nameChange["user"]!.GetValue<string>());
    }

    [Fact]
    public async Task Audit_entries_are_immutable_at_the_database_level()
    {
        var admin = await ClientAsync();
        await CreateAsync(admin, new { name = Unique("Immut"), activateImmediately = true, areas = new[] { Area("ID", statewide: true) } });

        using var conn = new SqliteConnection($"Data Source={api.DbPath}");
        conn.Open();
        foreach (var sql in new[] { "UPDATE AuditEntries SET User = 'tamper'", "DELETE FROM AuditEntries" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            Assert.Contains("immutable", ex.Message);
        }
    }

    // ---- JSON + XML (FR-11) ----

    [Fact]
    public async Task Evaluation_supports_xml_request_and_response()
    {
        var admin = await ClientAsync();
        await CreateAsync(admin, new { name = Unique("Xml"), activateImmediately = true, areas = new[] { Area("KS", zips: ["66101"]) } });

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/moratoriums/evaluate")
        {
            Content = new StringContent(
                "<evaluationRequest><transactionDateTime>2027-01-08T08:00:00Z</transactionDateTime><locations><location><locationId>LOC001</locationId><state>KS</state><zipCode>66101</zipCode></location></locations></evaluationRequest>",
                Encoding.UTF8, "application/xml"),
        };
        req.Headers.Add("X-Api-Key", ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var res = await api.CreateClient().SendAsync(req);
        var xml = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("xml", res.Content.Headers.ContentType!.MediaType);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("evaluationResponse", doc.Root!.Name.LocalName);
        Assert.Equal("true", doc.Root.Element("moratoriumApplicable")!.Value);
        Assert.Equal("LOC001", doc.Root.Element("affectedLocations")!.Element("affectedLocation")!.Element("locationId")!.Value);
        Assert.Equal("ZIP", doc.Root.Descendants("matchedCriteria").First().Element("type")!.Value);
    }

    [Fact]
    public void Fail_closed_response_matches_the_contract()
    {
        var json = JsonSerializer.SerializeToNode(MoratoriumManagement.Api.Services.EvaluationService.FailClosed(),
            MoratoriumManagement.Api.Api.Json.Options)!;
        Assert.Equal("UNAVAILABLE", json["evaluationStatus"]!.GetValue<string>());
        Assert.True(json.AsObject().ContainsKey("moratoriumApplicable"));
        Assert.Null(json["moratoriumApplicable"]);
        Assert.Equal("BLOCKING", json["displayMessageType"]!.GetValue<string>());
        Assert.Equal("MOR_API_UNAVAILABLE", json["errorCode"]!.GetValue<string>());
        Assert.True(json["retryAllowed"]!.GetValue<bool>());
    }
}
