<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no padding. Bold sparingly.
-->

# Commander API integration — plan & handoff note

> Author: 2026-04-30 (handoff from the env-var hardening session, before any code is written)
> Status: **NOT STARTED.** Env-var slots and security guard rails prepared; controller/service/DTOs do not exist yet.
> Companion docs: `SECRETS.md` (env-var matrix), `PROJECT_NOTES.md` (general project context), `CHAT_HANDOFF.md`.

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

## Open questions to ask the customer FIRST (do not start writing code until answered)

1. **What does Commander do?** Attendance? Payroll? Something else? What problem does the integration solve for the customer that the existing app doesn't?
2. **Which Commander endpoints do we need to call?** Read-only or read+write?
3. **What's the auth flow?** Basic auth on every request, OAuth client-credentials, login → bearer token with TTL, custom header? Get the docs link.
4. **One Commander account total, or one per employee?** If per-employee: how do we discover / map them?
5. **What does the customer want to see in our app?** A page that shows synced data? A button that pushes our data to Commander? A scheduled background sync?
6. **Frequency.** On demand (a button), on event (after each clock-in?), or scheduled (every N minutes / nightly)?
7. **Failure mode.** If Commander is down, do we queue and retry, or surface the error to the user immediately?
8. **Test/sandbox account.** Does the customer have non-production Commander credentials we can use during development?

Until Q1–Q3 are answered, do not ship anything beyond the placeholder env slots.

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

## Suggested architecture (validate after Q1–Q3 are answered)

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

---

## Feature-flag plan

Match the Notifications pattern from V1.3.0:

1. Add `CommanderIntegration` to the `knownFlags` array in `Program.cs` so it gets seeded as `Enabled = false` on first run of the Commander work.
2. Apply `[RequireFeatureOrSuperAdmin("CommanderIntegration")]` to `CommanderController`.
3. Add a `commanderIntegration` signal to `FeatureFlagService` and gate any new UI behind it.
4. Add a second toggle to the "Funkcie" card on the Account page.
5. Customer never sees the feature until the superadmin flips it on.

---

## What to put in front of the customer before code

1. The seven open questions above, especially Q1 (what does Commander do?) and Q3 (auth flow + docs link).
2. Ask whether they want a sandbox / test Commander account so we don't bang on prod during development.
3. If Commander has any rate limits or per-customer quotas, get them in writing.

---

## Definition of done (for the eventual Commander M1)

- [ ] Q1–Q8 answered and recorded in this file.
- [ ] `CommanderClient` exists, reads creds from `IConfiguration`, throws on missing config.
- [ ] No `Console.WriteLine` / `_logger.Log...` call references `Commander:Password` or its alias.
- [ ] Outgoing requests use HTTPS; reject other schemes at startup.
- [ ] `CommanderController` is `[Authorize]` and `[RequireFeatureOrSuperAdmin("CommanderIntegration")]`.
- [ ] `CommanderIntegration` flag seeded as `false`; flipping it on in dev's Funkcie card surfaces the new UI; flipping off hides it.
- [ ] Frontend never receives the credential (verify by inspecting the `CommanderDtos.cs` file before merging).
- [ ] `SECRETS.md` "Adding the Commander integration (later)" section updated to reflect what was actually built (env vars used, base URL var name, etc.).
- [ ] `PROJECT_NOTES.md` and `BACKLOG.md` updated.
- [ ] At least one end-to-end test from Vercel dev preview → Railway dev → Commander sandbox.

---

*End of plan. When the new chat starts, the assistant should read this file, then `SECRETS.md`, then ask the seven open questions above before touching any code.*
