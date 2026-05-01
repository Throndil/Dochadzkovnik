<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no padding. Bold sparingly.
-->

# Commander API integration — plan & handoff note

> Author: 2026-04-30 (handoff from the env-var hardening session, before any code is written)
> Updated: 2026-05-01 (Q1–Q8 answered as a working draft, pending customer review of the implementation)
> Status: **Q&A CLOSED (working draft), CODE NOT STARTED.** Env-var slots and security guard rails prepared; controller/service/DTOs do not exist yet. Answers in §2 are the developer's working assumptions; the customer will validate by looking through the implementation.
> Companion docs: `SECRETS.md` (env-var matrix), `PROJECT_NOTES.md` (general project context), `CHAT_HANDOFF.md`.
> Reference materials: `docs/CommanderAPI-REST_API_v1_specification_2024.pdf` — the official Commander REST API v1 spec (34pp, Oct 2024). Treat as authoritative for endpoint shapes, auth, and rate limits.

---

## What's already done (so you don't redo it)

Pre-allocated, but not yet consumed by any code:

- `API/appsettings.json` carries empty placeholder slots:
  ```
  "Commander": {
    "Username": "",
    "Password": ""
  }
  ```
  Empty strings only. Real values come from env, never from this file.
- `API/appsettings.Local.example.json` carries the same empty slots so devs see the shape when they copy the example to `appsettings.Local.json`.
- `SECRETS.md` documents `Commander__Username` / `Commander__Password` as **Required when used**. The "Adding the Commander integration (later)" section there lists the security non-negotiables — read it before writing the first line of code.

---

## Open questions — answered (working draft, 2026-05-01)

These are the developer's answers based on the customer's brief and the official API spec (`docs/CommanderAPI-REST_API_v1_specification_2024.pdf`). The customer will confirm by looking through the shipped implementation. If any answer turns out to be wrong, update this section first, then adapt the code.

1. **What does Commander do?** Vehicle / fleet management. The customer uses Commander to manage their company vehicles (gps, rides, refuelling, drivers). The integration enriches our existing `Car` records with live data Commander has and we don't: current odometer, last-known GPS position, the rides that vehicle did today, etc. None of this is currently visible in our app — workers and managers have to log into Commander separately. The integration solves the "two-tab problem".

2. **Which Commander endpoints do we need to call?** **Read-only.** The Commander v1 API surface is read-only by design — every documented endpoint is a `GET`. Endpoints relevant to us (in roughly the order we'll use them):
   - `GET /vehicles` — list of vehicles (the spec explicitly warns this should be cached and called at most once per day; abusing it can get the account rate-limited).
   - `GET /vehicles/{vehicleId}` — single vehicle detail.
   - `GET /last-positions` — current GPS position for every vehicle the API user can see.
   - `GET /current-tacho/{vehicleId}` — current odometer (km) and engine hours for a single vehicle. Direct fit for the "kilometres driven today" feature when paired with the start-of-day reading we cache locally.
   - `GET /rides/{vehicleId}?datetimeStart=…&datetimeEnd=…` — completed rides for a vehicle in a date range. Useful to attribute a ride to the worker who clocked in with that car.
   - `GET /drivers` — driver list, only if we want to cross-reference Commander driver IDs to our employees later.
   - Out of scope for now: `/all-rides`, `/waypoints`, `/waypoint-groups`, `/contracts`, `/cost-centers`, `/refueling-import`, `/deletedVehicles`. Listed in the spec, not on the current roadmap.

3. **Auth flow?** **HTTP Basic Auth** on every request, header `Authorization: Basic base64(username:password)`. No token, no refresh, no OAuth. Username/password are the customer's Commander API credentials (single shared account — see Q4). Spec is in `docs/CommanderAPI-REST_API_v1_specification_2024.pdf`. **Implication: the password is sent on every request, so the security non-negotiables in §"Security non-negotiables" below apply on every code path that touches the HttpClient — no exceptions.**

4. **One Commander account total, or one per employee?** **One shared customer account.** All API calls use the customer's single Commander credential pair. Workers never see, type, or otherwise interact with the credential. No per-employee mapping is required for read-only fleet data; if we later need to attribute Commander rides to one of our employees, we do it on our side via the `Car ↔ Employee` relationship that already exists on `TimeEntry`. **Security implication: this credential is the keys-to-the-fleet for the whole company. The non-negotiables in the next section are non-negotiable.**

5. **What does the customer want to see in our app?** Initial scope is whatever the developer thinks is useful — the customer trusts us to propose. Working plan:
   - **M1 (read-only fleet view).** A car-detail panel addition that shows, for the selected vehicle: live odometer, last-known GPS position with map link, last-communication timestamp, whether the ignition was on at last sample. Pulled on-demand when a manager opens that car.
   - **M2 (km-driven attribution).** When a manager opens a `TimeEntry` that has a `Car` attached, show the kilometres that vehicle clocked between that worker's `ClockIn` and `ClockOut` (computed as `tachoAt(ClockOut) − tachoAt(ClockIn)`, or fetched from `/rides/{vehicleId}` for the same window). Optionally the start and end GPS positions for that window.
   - **M3 (ride list).** A "Jazdy" tab on car-detail showing completed Commander rides for a date range, optionally attributed to whoever was clocked in on our side at the time.
   - Anything beyond M3 is unscoped until the customer reacts to M1.

6. **Frequency?** **On-demand only, no background sync.** A request to Commander only happens when a manager actively opens a car-detail panel or a time-entry detail that needs Commander data. Reasons: (a) the spec explicitly discourages frequent polling and rate-limits at 300 req/window per company, (b) we don't need to surface anything to workers (no kiosk-side use case), (c) on-demand keeps the security surface tight — no background process holding the credential. Cache the `/vehicles` list for 24 hours per the spec's own guidance; cache `/last-positions` for ~30 seconds within a single manager's session so refreshing the panel doesn't burn quota. Everything else is fetched live each time the panel opens.

7. **Failure mode if Commander is down?** **Surface the error to the user, then silently retry in the background.** The manager sees a clear Slovak message ("Commander momentálne nedostupný — pokus o obnovenie…") with a small retry indicator; the panel continues to show the most recent successfully-fetched data (cache fallback) if any. Behind the scenes the client retries once after a short delay (e.g. 2–4s with jitter). On 429, honour `Retry-After` exactly. Errors from Commander never leak the credential or internal IDs to the frontend (see security non-negotiables below).

8. **Sandbox / non-prod credentials?** **Real customer credentials are available for development.** No separate sandbox environment exists for Commander. **Implication: the dev environment is hitting the customer's live fleet data the moment we wire this up.** Reads only, no writes — but the rate-limit and PII concerns are real. Rules that follow from this:
   - Local dev creds live in `appsettings.Local.json` (gitignored) only. Never in the repo.
   - Railway dev env gets the same creds via Railway env vars — **never** committed.
   - The dev environment must respect the same rate limits and the same once-per-day cache discipline as prod.
   - Logging discipline (§"Security non-negotiables" below) applies in dev too, not just prod.

The work is unblocked. Proceed to the architecture section below; the customer reviews the implementation, not the questionnaire.

---

## Security non-negotiables (apply regardless of what Commander turns out to be)

1. **Credentials live ONLY in `Configuration["Commander:Username"]` / `Configuration["Commander:Password"]`** which read from `Commander__Username` / `Commander__Password` env vars on Railway, or `appsettings.Local.json` for local dev. They never enter `appsettings.json`, never enter migration files, never enter `.csproj`, never enter any committed config.
2. **Never log the password.** Not at any `LogLevel`. Not "redacted" with the first letter visible. Not on exception paths. The password string never appears in any structured-logging argument list.
3. **Never log the username at `Information` or below.** It's PII for the customer's internal account. `Debug` only, behind an explicit "I'm debugging" feature flag if you really need it.
4. **Never serialize either value into a DTO that's returned to the frontend.** The Angular app must never see Commander credentials. If the frontend needs to display "connected as X", the backend exposes a separate masked field or just a boolean `connected: true`.
5. **Don't store the password in the database.** If Commander returns a session token, store the token (with expiry) in a dedicated `CommanderSession` row or in-memory cache. The plaintext password stays only in the env var.
6. **Outgoing HTTP must be HTTPS.** Reject any Commander base URL that doesn't start with `https://` at startup.
7. **No "test connection" endpoint that echoes the credential back.** The connectivity check returns a status only.
8. **No retry-with-creds-in-URL.** Commander basic auth (if used) goes in the `Authorization` header, never the query string.
9. **Errors from Commander must be sanitised before they reach the frontend.** Strip any echoed credentials or internal IDs.
10. **Consider a feature flag.** Same pattern as Notifications — a `CommanderIntegration` row in `FeatureFlags` so we can ship the controller hidden, switch it on per environment via the Funkcie card on the Account page, and turn it back off without a redeploy if something goes wrong.

---

## Commander API specifics (from the official spec)

Source: `docs/CommanderAPI-REST_API_v1_specification_2024.pdf` (v1, Oct 2024). When in doubt, the PDF wins; this section is a cheat-sheet, not a substitute.

- **Base URL.** `https://online.commander-systems.com/api/v1` — every endpoint is rooted here. Stored as `Commander:BaseUrl` config (env var `Commander__BaseUrl`).
- **Auth.** HTTP Basic. `Authorization: Basic base64(Username:Password)` header on every request. No login/refresh dance.
- **Content type.** `application/json` request and response. `GET`-only — no request bodies are accepted by any endpoint.
- **Rate limits.** 300 req per window, per company (authenticated) / per IP (unauthenticated). Response headers `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` are present on every successful response. Hitting the limit returns **HTTP 429** with a `Retry-After` header. The client must respect `Retry-After` exactly — no shorter retries.
- **Error shape.** 4xx/5xx responses follow `{ "status": "error", "message": "<text>" }`. The `message` is a Commander internal string; it must be **sanitised** before reaching the frontend (see security non-negotiables — strip any echoed credential or internal IDs).
- **Caching guidance from the spec.** The vehicles list endpoint warns explicitly: *"Service is intended to be used only to read or update vehicle list and it should be called e.g. once a day. Accounts calling these service all the time can have limited access for the services."* We MUST cache `/vehicles` for ≥24h on our side.
- **Numeric parsing gotcha.** The spec warns that numeric fields can come back as strings, can use a comma decimal separator, and that empty values can be `""`, `null`, or `0` — but `0` must be treated as a real value, not as empty. DTOs and parsers must handle all three.
- **Endpoints we plan to use** (full list and shapes are in the PDF):
  - `GET /vehicles` — daily-refresh cached list. Returns array under `vehicles` with `vehicleId`, `vehicleName`, `vehicleRegistrationPlate`, `vin`, `lastCommunication`, etc.
  - `GET /vehicles/{vehicleId}` — single-vehicle detail.
  - `GET /last-positions[?page=&limit=]` — paginated list (max `limit=1000`, or `100` if addresses are enabled). Returns `positions[]` with `gpsTime`, `gpsLat`, `gpsLon`, `carIgnition`, CANBUS values, optional `address`.
  - `GET /current-tacho/{vehicleId}` — `{ "currentTacho": { "km": <float>, "engine_hours": <float> } }`. The single most useful endpoint for the "kilometres driven during this shift" feature.
  - `GET /rides/{vehicleId}?datetimeStart=…&datetimeEnd=…` — completed rides for a vehicle in a date window. Use to attribute rides to a worker's clock-in window when the panel needs it.
  - `GET /drivers` — only if we want Commander driver IDs (not in the M1/M2 plan).
- **Endpoints out of scope (M1–M3).** `/all-rides`, `/waypoints`, `/waypoint-groups`, `/contracts`, `/cost-centers`, `/refueling-import`, `/deletedVehicles`. Listed for completeness; not on the roadmap.

---

## Suggested architecture

```
appsettings.json                ← empty slots only, committed
appsettings.Local.json          ← dev creds, gitignored
Railway env (prod / dev)        ← real creds, never in code

           Configuration["Commander:Username"/"Commander:Password"]
                                    │
                                    ▼
                        API/Services/ICommanderClient.cs       ← interface
                        API/Services/CommanderClient.cs        ← typed HttpClient
                                    │
                            depends only on the IConfiguration
                            and ILogger; reads creds at request time
                                    │
                                    ▼
                        API/Controllers/CommanderController.cs
                          [Authorize] (admin only — superadmin bypass via the
                          existing [RequireFeatureOrSuperAdmin] filter)
                                    │
                                    ▼
                        Frontend Angular service (read-only data;
                        triggers actions; NEVER receives credentials)
```

### Files to create (when work starts)

```
API/
  Services/
    ICommanderClient.cs          ← interface
    CommanderClient.cs           ← HttpClient + auth, no logging of creds
  Controllers/
    CommanderController.cs       ← [Authorize] + [RequireFeatureOrSuperAdmin("CommanderIntegration")]
  DTOs/
    CommanderDtos.cs             ← request/response shapes; NEVER include creds
  Models/
    CommanderSession.cs?         ← optional, only if Commander uses a session token

client/
  src/app/services/commander.service.ts
  src/app/pages/commander/...    ← if there's a UI surface
```

### `Program.cs` wiring (sketch)

```csharp
builder.Services.AddHttpClient<ICommanderClient, CommanderClient>(c =>
{
    var baseUrl = builder.Configuration["Commander:BaseUrl"]
        ?? throw new InvalidOperationException("Commander:BaseUrl not configured");
    if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Commander:BaseUrl must use HTTPS");
    c.BaseAddress = new Uri(baseUrl);
});
```

`CommanderClient` reads username/password from `IConfiguration` per request (so a Railway env-var rotation takes effect without restart). On a missing / empty value, throw an explicit `InvalidOperationException` with a message naming the env var.

The HttpClient also needs:
- A typed `AuthenticationHeaderValue("Basic", base64(user:pass))` set per outbound call (not via a `DefaultRequestHeaders` line that would persist on a singleton client).
- A request handler / delegating handler that detects `429` + `Retry-After` and surfaces it as a typed result (not a swallowed exception) so the controller can pass it back to the frontend with the correct retry hint.
- An in-memory cache for `/vehicles` keyed by company (24h TTL) and a short cache for `/last-positions` (~30s TTL) — see Q6 in §2.

---

## Feature-flag plan

Match the Notifications pattern from V1.3.0:

1. Add `CommanderIntegration` to the `knownFlags` array in `Program.cs` so it gets seeded as `Enabled = false` on first run of the Commander work.
2. Apply `[RequireFeatureOrSuperAdmin("CommanderIntegration")]` to `CommanderController`.
3. Add a `commanderIntegration` signal to `FeatureFlagService` and gate any new UI behind it.
4. Add a second toggle to the "Funkcie" card on the Account page.
5. Customer never sees the feature until the superadmin flips it on.

---

## What to put in front of the customer (after M1 ships)

The customer's stated preference is to validate by reviewing the implementation rather than answering a questionnaire up-front. So:

1. Ship M1 (read-only fleet view on car-detail) hidden behind the `CommanderIntegration` feature flag.
2. Walk the customer through the M1 surface in a demo. Capture their reaction in `PROJECT_NOTES.md`.
3. Specifically confirm with them:
   - That the data we're surfacing (live odometer, last-known GPS, last-communication timestamp) is the right starting set.
   - That they're comfortable with on-demand fetching only (no background sync, no kiosk-side surface).
   - That the failure UX (Slovak "Commander momentálne nedostupný", silent retry, cache fallback) reads correctly to a non-tech user.
4. Only then size M2 (km-driven attribution per `TimeEntry`) and M3 (rides tab).

---

## Definition of done (Commander M1)

- [x] Q1–Q8 answered and recorded in this file (working draft, 2026-05-01; customer confirms by reviewing the implementation).
- [ ] `CommanderClient` exists, reads creds from `IConfiguration`, throws on missing config.
- [ ] No `Console.WriteLine` / `_logger.Log...` call references `Commander:Password` or its alias.
- [ ] Outgoing requests use HTTPS; reject other schemes at startup.
- [ ] `CommanderController` is `[Authorize]` and `[RequireFeatureOrSuperAdmin("CommanderIntegration")]`.
- [ ] `CommanderIntegration` flag seeded as `false`; flipping it on in dev's Funkcie card surfaces the new UI; flipping off hides it.
- [ ] Frontend never receives the credential (verify by inspecting the `CommanderDtos.cs` file before merging).
- [ ] `429` + `Retry-After` is honoured exactly; `/vehicles` cached ≥24h; `/last-positions` cached ~30s.
- [ ] `SECRETS.md` "Adding the Commander integration (later)" section updated to reflect what was actually built (env vars used, base URL var name, etc.).
- [ ] `PROJECT_NOTES.md` and `BACKLOG.md` updated.
- [ ] At least one end-to-end test from Vercel dev preview → Railway dev → Commander prod (no sandbox available — see Q8). Use a single low-impact `GET /vehicles` call as the smoke test.

---

*End of plan. The eight open questions are answered as a working draft (§2, 2026-05-01). Code can begin against the architecture below; the customer validates the result by walking through M1.*
