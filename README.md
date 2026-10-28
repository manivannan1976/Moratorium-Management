# Moratorium Management

Broker Portal module for E&S Small Commercial that lets business users create, schedule, activate, lift and audit Property
moratoriums, and exposes APIs that Duck Creek calls to evaluate Property locations.
Source of truth: `PSFS-Broker Portal - E&S Small Commercial - Moratorium Management-281026-152929.pdf`.

| Layer | Tech |
|---|---|
| API | .NET 10, ASP.NET Core controllers, EF Core + SQLite, JWT + API-key auth, Swagger |
| UI | Angular 20 (standalone components, signals) |
| Tests | xUnit integration tests over the real HTTP pipeline |

```
src/MoratoriumManagement.Api   API, domain, EF Core, scheduler, static hosting of the UI
tests/MoratoriumManagement.Tests
web/                            Angular app
scripts/                        deploy.ps1, stop.ps1, dev.ps1
```

## Run locally

Prerequisites: .NET 10 SDK, Node 20+.

```powershell
scripts\deploy.ps1        # test, build UI + API, publish to deploy\app, start on http://localhost:5080
scripts\stop.ps1
scripts\dev.ps1           # dev mode: API hot reload :5080 + ng serve :4200
```

Windows Smart App Control must be off (or the build signed), otherwise Windows blocks locally built .NET assemblies.

| | |
|---|---|
| App | http://localhost:5080 |
| Swagger | http://localhost:5080/swagger |
| Administrator | `admin@portal.local` / `Admin@12345` |
| Viewer | `viewer@portal.local` / `Viewer@12345` |
| Duck Creek key | header `X-Api-Key: duckcreek-local-dev-key` (read + evaluate only) |

Demo data (Florida, Hurricane Milton, a scheduled California wildfire, a draft, a lifted moratorium) is seeded on first
start. All of the above is local-dev configuration in `appsettings.json`; change `Jwt:SigningKey`, `Integration:ApiKey`
and the seed users before any shared deployment. The SQLite file lives in `storage/moratorium.db` next to the app.

## APIs

| Purpose | Endpoint | Role |
|---|---|---|
| Sign in | `POST /api/auth/login` | anonymous |
| List / active list | `GET /api/moratoriums?status=Active\|Scheduled\|Inactive\|Draft`, `GET /api/moratoriums/active` | Admin, Viewer, API key |
| Details | `GET /api/moratoriums/{id}` | Admin, Viewer, API key |
| Create / update | `POST /api/moratoriums`, `PUT /api/moratoriums/{id}` | Admin |
| Activate / deactivate | `POST /api/moratoriums/{id}/activate`, `.../deactivate` | Admin |
| Add / remove geography | `POST /api/moratoriums/{id}/geographies`, `.../geographies/remove` | Admin |
| Audit history | `GET /api/moratoriums/{id}/audit`, `GET /api/audit` | Admin, Viewer |
| **Evaluation** | `POST /api/moratoriums/evaluate` | Admin, Viewer, API key |
| Reference data | `GET /api/reference/states`, `.../states/{code}/counties`, `POST /api/reference/counties` (Admin) | |

Every response can be returned as XML (`Accept: application/xml` or `?format=xml`), and the evaluation request can be
posted as XML (`Content-Type: application/xml`), for Duck Creek. Arrays are wrapper + singular child elements, for example
`<locations><location>…</location></locations>`.

Evaluation outcomes: `200 SUCCESS` (match or no match), `400 FAILED` (`INVALID_LOCATION_DATA` with per-field
`validationErrors`), `503 UNAVAILABLE` (fail closed: `BLOCKING`, `MOR_API_UNAVAILABLE`, `retryAllowed: true`).

## Requirement coverage

| Requirement | Where |
|---|---|
| BR-01, FR-08 roles | Admin / Viewer policies on every endpoint; UI hides Create/Edit/Activate for Viewers |
| BR-02, FR-05 multiple active | no exclusivity; evaluation returns every match |
| BR-03 Property only | the API only evaluates Property locations; no other line of business exists |
| BR-05, BR-06 | no override feature; Duck Creek enforcement is out of scope |
| BR-07 | no CSV upload; paste/bulk text areas instead |
| FR-01, SD-01…03 | create (immediate, scheduled or draft), update, activate, deactivate; dashboard with Active / Scheduled / Inactive (+ Drafts for admins), Show More |
| FR-02, FR-03 | `Services/MoratoriumService.cs` + `Geography/GeographyCatalog.cs`: 2-letter states, county must belong to the state (3,235 Census counties seeded), ZIP exactly 5 digits as text so leading zeros survive; values normalised before save |
| FR-04, SD-05 | `AuditEntries` is append-only; SQLite triggers reject UPDATE/DELETE |
| FR-05A | `MoratoriumScheduler` flips Scheduled→Active and Active→Inactive every 15 s (audited as `System`); evaluation also checks the time window itself, so it is correct between ticks |
| FR-06, FR-07, SD-04 | detail page groups by state, collapsible, searchable |
| FR-09, FR-10, FR-11 | `Services/EvaluationService.cs`; message text in `Api/Contracts.cs` |

## Decisions and assumptions

- **Evaluation time.** A moratorium applies if it is active *now* (server clock). `transactionDateTime` is validated but not used for matching, so a lifted moratorium never matches a back-dated transaction.
- **Locations.** `state` and `zipCode` are required; `county` is optional and, if sent, must belong to the state.
- **Messages.** Where the PDF is inconsistent (e.g. "BHSI" in the matrix vs "Property" in the JSON samples, or three wordings for the ZIP error) the JSON sample wording is used for evaluation responses and the Administration Error Matrix wording for admin responses.
- **Names.** Unique (case-insensitive) among Active and Scheduled moratoriums only, as specified.
- **Time zones.** Times are stored in UTC with the chosen IANA zone; the UI shows and edits them in that zone.
- **Auth.** Local JWT login stands in for the Broker Portal's identity provider; swap the `AuthController`/JWT setup for the portal's SSO when integrating. The header's other menu items (Underwriting Guidelines, User Management) are placeholders owned by other modules.
- **Database.** SQLite via `EnsureCreated` for a zero-setup local run; move to SQL Server and EF migrations for shared environments.
