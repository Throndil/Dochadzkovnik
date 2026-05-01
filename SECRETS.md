<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no padding. Bold sparingly.
-->

# Secrets & Environment Variables

> Last updated: 2026-04-30 (env-var refactor + Commander placeholder)
> Authoritative reference for every secret the API reads, where to set it, and what the operator does on a fresh Railway environment.

## Ground rules

1. **Nothing in `appsettings.json`.** All credential fields in the committed `appsettings.json` are intentionally empty strings. Any non-empty value in that file is a bug.
2. **Production source of truth = Railway env vars.** Local dev source of truth = `appsettings.Local.json` (gitignored).
3. **ASP.NET Core's nested-key env convention requires double underscore.** `Jwt__Key` maps to `Jwt:Key`. Single underscore (`Jwt_Key`) is silently ignored. This trap has bitten us once already (2026-04-30) and the consequence was running prod with a leaked placeholder JWT key.
4. **Fail loud on missing Jwt:Key.** The API throws on startup if the key is absent or shorter than 32 bytes. Better to crash visibly than to sign tokens with nothing.
5. **No fallback credentials in code.** If `AdminSeed` / `SuperAdminSeed` aren't configured, the seed is skipped with a console warning; existing users keep their existing passwords.
6. **Customer credentials (Commander) are env-only from day one.** They never enter `appsettings.json`, never enter migration files, never enter logs.

---

## Required env vars (Railway prod + dev)

| Env var | Maps to config key | Purpose | Notes |
|---|---|---|---|
| `Jwt__Key` | `Jwt:Key` | HMAC-SHA256 signing key for JWT tokens | **Required.** ≥32 bytes. Generate with `openssl rand -base64 48` or `[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))` in PowerShell. Different per environment. |
| `Jwt__Issuer` | `Jwt:Issuer` | JWT `iss` claim | `Dochadzkovnik` |
| `Jwt__Audience` | `Jwt:Audience` | JWT `aud` claim | `Dochadzkovnik` |
| `DATABASE_URL` | (direct env read) | PostgreSQL connection string | Railway-provided. Format: `postgres://user:pass@host:port/db`. |
| `AdminSeed__Username` | `AdminSeed:Username` | Customer-facing admin login | `vladosroka` in prod. |
| `AdminSeed__Password` | `AdminSeed:Password` | Customer-facing admin password | Real customer password. Resets on every deploy to whatever this value is. |
| `SuperAdminSeed__Username` | `SuperAdminSeed:Username` | Internal "feature flag" admin login | `admin`. |
| `SuperAdminSeed__Password` | `SuperAdminSeed:Password` | Internal superadmin password | Different from AdminSeed. |
| `Cloudinary__CloudName` | `Cloudinary:CloudName` | Cloudinary account | |
| `Cloudinary__ApiKey` | `Cloudinary:ApiKey` | Cloudinary API key | |
| `Cloudinary__ApiSecret` | `Cloudinary:ApiSecret` | Cloudinary API secret | |
| `AllowedOrigins__0` | `AllowedOrigins:0` | CORS origin (Vercel prod URL) | Use one indexed slot per origin. |
| `AllowedOrigins__1` | `AllowedOrigins:1` | CORS origin (Vercel preview URL) | |
| `VAPID_PUBLIC_KEY` | (direct env read) | Web push public key | Auto-generated to DB on first run if both Public+Private env are blank. Manually generate with `npx web-push generate-vapid-keys`. |
| `VAPID_PRIVATE_KEY` | (direct env read) | Web push private key | |
| `VAPID_SUBJECT` | (direct env read) | mailto: identifier for push provider | E.g. `mailto:support@profistav.sk` |

## Required when used

| Env var | Maps to | Purpose | Required when |
|---|---|---|---|
| `Email__Host` | `Email:Host` | SMTP host for password-reset emails | Forgot-password feature is in use. If blank, the API logs the reset link instead of mailing it. |
| `Email__Port` | `Email:Port` | SMTP port | Default `587`. |
| `Email__Username` | `Email:Username` | SMTP auth user | |
| `Email__Password` | `Email:Password` | SMTP auth password | |
| `Email__From` | `Email:From` | From address | |
| `AppUrl` | `AppUrl` | Base URL used in password-reset links | E.g. `https://sichtovnica.vercel.app`. |
| `Commander__BaseUrl` | `Commander:BaseUrl` | Commander REST API base URL | **When the Commander integration ships.** Production value is `https://online.commander-systems.com/api/v1` (per the official spec, `docs/CommanderAPI-REST_API_v1_specification_2024.pdf`). Must start with `https://` — startup throws otherwise. No sandbox URL exists. |
| `Commander__Username` | `Commander:Username` | Customer's Commander API account | **When the Commander integration ships.** Empty until then. Single shared customer account; HTTP Basic auth on every request. |
| `Commander__Password` | `Commander:Password` | Customer's Commander API password | Same. **Never** logged at any level; never returned in any DTO. |

---

## Local development

1. Copy `API/appsettings.Local.example.json` to `API/appsettings.Local.json`.
2. Fill in **dev-only** values. Use any 32+ char string for `Jwt:Key` — local dev doesn't need a real secret. Use throwaway passwords for `AdminSeed` / `SuperAdminSeed`.
3. **Never commit `appsettings.Local.json`.** It's gitignored (`/appsettings.Local.json`, `**/appsettings.Local.json`).
4. `dotnet run` from `API/` picks it up automatically — `Program.cs` does `AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)` after the default configuration sources.

Configuration precedence (highest wins):
```
Railway / OS env vars
        ↓
appsettings.Local.json     ← gitignored, dev only
        ↓
appsettings.{Environment}.json
        ↓
appsettings.json           ← committed, no secrets
```

---

## What's already exposed (private repo, accept residual risk)

- `Nikolasko1` was committed in `09d4d22 (Big photo update)` as the `AdminSeed:Password` default in `appsettings.json` and as a `?? "Nikolasko1"` fallback in `Program.cs`. It is the operational customer password and stays as-is per owner decision (2026-04-30).
- `Superadmin12345!!` was committed earlier in this same session (2026-04-30) as the `SuperAdminSeed:Password` fallback in `Program.cs`. Same status.
- `vladosroka` username is similarly in history. Not a secret in itself.
- The repo is private with a fixed two-person collaborator list, so the practical exposure is the team.
- All three values are now removed from `appsettings.json` and from `Program.cs` fallbacks. Future commits cannot leak them again because the seed code reads exclusively from configuration.

If a third collaborator is ever added, audit `Settings → Collaborators` and rotate the AdminSeed and SuperAdminSeed passwords before granting access.

---

## Adding the Commander integration (later)

> Updated 2026-05-01 with answers from `COMMANDER_PLAN.md` §2 (working draft).

What the integration is: a **read-only** consumer of the customer's Commander fleet-management API (`https://online.commander-systems.com/api/v1`). HTTP Basic auth on every request, single shared customer account. Spec is preserved at `docs/CommanderAPI-REST_API_v1_specification_2024.pdf`.

When the Commander API controller is implemented:

1. Set `Commander__BaseUrl`, `Commander__Username`, and `Commander__Password` in Railway (prod and dev). **No sandbox exists**, so dev points at the same production Commander instance — see §"Sandbox" below before wiring this up.
2. The client reads them via `Configuration["Commander:BaseUrl" / ":Username" / ":Password"]`.
3. **Never** log Username or Password. Never include either in error responses. Never include either in any DTO returned to the frontend.
4. There is no token / session model in Commander v1 — every request carries Basic Auth. The plaintext password lives only in env / `appsettings.Local.json`. Do not write it to the database.
5. Reject startup if `Commander:BaseUrl` does not begin with `https://`.
6. Honour `Retry-After` exactly on 429 responses. Cache `/vehicles` for ≥24h (the spec explicitly warns about over-calling it).
7. Sanitise Commander error messages before returning to the frontend (the `{"status":"error","message":"…"}` body can echo internal IDs).

### Sandbox

There is no Commander sandbox environment. Development hits the customer's live fleet data — read-only, but the rate-limit and PII concerns are real:

- Local dev creds in `appsettings.Local.json` only (gitignored). Never in the repo.
- Dev Railway env gets the same creds via env vars; never committed.
- Dev environment respects the same caching discipline as prod (`/vehicles` once per day, `/last-positions` ~30s).
- Logging discipline applies in dev too. No `Console.WriteLine($"…{username}…")` "just for debugging" — there is no isolated environment that makes that safe.

---

## What to do on a fresh Railway environment

1. Generate `Jwt__Key`: `openssl rand -base64 48` (or PowerShell equivalent).
2. Set every env var marked **Required** in the table above.
3. Optionally set the **Required when used** vars (Email, Commander) once those features are needed.
4. Deploy. On boot the API will:
   - Throw with a clear message if `Jwt__Key` is missing or too short.
   - Log a warning and skip seed if `AdminSeed` / `SuperAdminSeed` are missing (existing users preserved; new fresh DB has no admin until you fix env).
   - Auto-generate VAPID keys to the DB if both `VAPID_PUBLIC_KEY` and `VAPID_PRIVATE_KEY` are blank.
5. Log in once as `admin` (superadmin), open Account → Funkcie, flip feature flags as needed.
