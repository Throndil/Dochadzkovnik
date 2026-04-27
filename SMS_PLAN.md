<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# SMS Reminders — Comprehensive Plan

> ⚠️ **SUPERSEDED 2026-04-26 — see `NOTIFICATIONS_PLAN.md`.**
> The customer's workforce is older / non-tech-savvy and SMS provider cost was a concern.
> V1 of reminders will use **PWA push notifications** as the primary channel and **WhatsApp Business Cloud API** as the secondary channel — both effectively free.
> Single trigger only: "worker has not clocked any hours in the past 2 days".
> SMS is parked as a V2 fallback if push + WhatsApp coverage proves insufficient. Everything below is preserved as reference material for that V2 work.

---

> Author: planning chat, 2026-04-26
> Status: **DEFERRED to V2 — superseded by NOTIFICATIONS_PLAN.md**
> Companion docs: `CHAT_HANDOFF.md`, `PROJECT_NOTES.md` (§ "Notifications"), `BACKLOG.md` (§ "Notifications & Reminders")

This plan turns the high-level SMS skeleton from `CHAT_HANDOFF.md` into a concrete, phased implementation that respects every existing rule of the codebase: Slovak-only copy, `Europe/Bratislava` timezone, PWA hygiene, and the Migration Safety Rules. **No code is written until the customer answers the questions in §1.**

---

## 1. Open Questions for the Customer (must be answered first)

Each answer maps to a concrete decision in §3–§5. The cost of asking these up-front is one short call; the cost of guessing wrong is rebuilding the trigger engine and copy library.

| # | Question | Why it matters | Default if customer is unsure |
|---|---|---|---|
| Q1 | Which trigger fires **first**? (a) clock-in nudge, (b) clock-out nudge, (c) missing-photo nudge, (d) 48h-no-activity nudge, (e) admin alert? | Determines what we build in M2 vs. defer to M5. | (d) 48h-no-activity, because it's already in BACKLOG.md and is the cheapest signal. |
| Q2 | What time(s) of day should each reminder fire? (`Europe/Bratislava`) | Drives the BackgroundService schedule and the admin "Notifikácie" page UI. | 07:30 (clock-in), 17:30 (clock-out), 18:00 (missing-photo). |
| Q3 | Working days only (Mon–Fri), or every day? Public holidays? | Determines whether we need a Slovak holiday calendar. | Mon–Fri; ignore holidays for V1, surface as V2 toggle. |
| Q4 | All employees, or opt-in / opt-out per employee? | Adds either an `Employee.SmsRemindersEnabled` boolean or a per-trigger join table. | Per-employee opt-out boolean (sensible default, avoids spam). |
| Q5 | Confirm exact Slovak copy for each reminder type. | Translations are cheap to draft, expensive to redo after delivery. | Drafts in §6 below — customer to approve / amend. |
| Q6 | Which SMS provider? Twilio / SMSAPI.sk / smsmanager.cz / o2 SMS Connector / something they already pay for? Budget per SMS? | Determines `ISmsService` impl and pricing constraints. ~30 workers × 1 SMS/day ≈ 900 SMS/month → at €0.06/SMS that's ~€54/month. | Twilio (battery-included, but ~€0.075/SMS to SK). Recommend SMSAPI.sk if cost matters (~€0.03/SMS). |
| Q7 | Phone-number validation: strict `+421` only, or any E.164? | One regex line vs. libphonenumber dependency. | Strict `+421` (matches the single-firm market); store normalised E.164. |
| Q8 | Should admin be CC'd (or alerted separately) when a worker no-shows? | Decides whether AdminAlert is a separate trigger or just an extra recipient on the worker's reminder. | Separate "Admin denný súhrn" trigger at 18:30, listing everyone who didn't clock today. |
| Q9 | Retention: how long do we keep `SmsReminder` history? | GDPR-relevant. SMS bodies may contain employee names. | 90 days, then auto-purge. Configurable in `appsettings`. |
| Q10 | Do they want a "Test SMS" admin button that sends to a manually entered number? | BACKLOG.md already has "Internal SMS reminder tester" as a separate item. | Yes, ship in M2 — it's the first manual gate before automating anything. |

**Action: schedule a 20-min call. Do not start writing code without answers to Q1, Q4, Q6, Q7.** Q5 copy can be iterated after first send.

---

## 2. What's Already in Place (good news)

These are the bits we don't have to build:

- **`Employee.PhoneNumber`** is already on the model (`API/Models/Employee.cs:10`) as nullable `string`. We do **not** need a migration just to store the phone number.
- **`Europe/Bratislava` time handling** is already centralised — see `KioskController` for the canonical pattern. Reuse the same `TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava")` lookup.
- **JWT admin auth** is in place. The new `SmsRemindersController` plugs in with `[Authorize]` like every other admin controller.
- **Self-heal infrastructure** in `Program.cs` covers both SQLite (`pragma_table_info` checks since V1.1.1) and PostgreSQL (`information_schema.columns` guards). Any new column gets a self-heal block alongside its EF migration.
- **No kiosk UI changes needed** — workers receive SMS on their personal phones, so we don't risk regressing the wall-mounted tablet flow.
- **Existing folders match the planned skeleton** — `API/Controllers/`, `API/Services/`, `API/Models/`, `API/Migrations/` all exist. Only `API/BackgroundServices/` is new.

---

## 3. Architecture

### 3.1 High-level diagram

```
                              ┌───────────────────────────────────────────┐
                              │  SmsReminderBackgroundService (1-min tick)│
                              │  - reads NotificationConfig from DB       │
                              │  - converts now → Europe/Bratislava       │
                              │  - dispatches matching triggers           │
                              └────────────────┬──────────────────────────┘
                                               │ for each trigger fired
                                               ▼
              ┌──────────────────────────────────────────────┐
              │  ReminderTriggerEvaluator                    │
              │  - queries TimeEntry / WorkPhoto             │
              │  - checks Employee.SmsRemindersEnabled       │
              │  - checks idempotency (SmsReminder table)    │
              │  - returns list<(employee, body, type)>      │
              └────────────────┬─────────────────────────────┘
                               │
                               ▼
              ┌──────────────────────────────────────────────┐
              │  ISmsService.SendAsync(toE164, body)         │
              │   └── TwilioSmsService (or SMSAPI.sk impl)   │
              └────────────────┬─────────────────────────────┘
                               │
                               ▼
              ┌──────────────────────────────────────────────┐
              │  SmsReminder row written: SentAt, ProviderId │
              │  Status=Sent | Failed | Throttled            │
              └──────────────────────────────────────────────┘
```

### 3.2 Data model

Two new tables, one new column on Employee.

**`SmsReminder`** — append-only audit log of every SMS attempt.
| Column | Type | Notes |
|---|---|---|
| Id | int PK |  |
| EmployeeId | int FK → Employees | nullable so admin-test sends can be logged |
| ToPhoneNumber | string | snapshotted E.164 at send time |
| Type | string (enum) | `ClockInNudge`, `ClockOutNudge`, `NoActivity48h`, `MissingPhoto`, `AdminDailySummary`, `AdminTest` |
| Body | string | up to 320 chars (2 SMS segments) |
| TriggerDate | date | the local Bratislava date the trigger represents — basis of the idempotency key |
| SentAt | DateTime UTC |  |
| Status | string (enum) | `Sent`, `Failed`, `Throttled`, `Skipped` |
| ProviderMessageId | string? | Twilio SID / SMSAPI ID |
| ErrorMessage | string? |  |

Unique index: `(EmployeeId, Type, TriggerDate)` to prevent duplicate sends if the BackgroundService restarts mid-tick.

**`NotificationConfig`** — single-row table holding the schedule. Single-row keeps the admin UI dead simple; we can split per-trigger later if needed.
| Column | Type | Notes |
|---|---|---|
| Id | int PK | always 1 |
| ClockInNudgeEnabled | bool |  |
| ClockInNudgeTime | TimeSpan | local Bratislava |
| ClockOutNudgeEnabled | bool |  |
| ClockOutNudgeTime | TimeSpan |  |
| NoActivity48hEnabled | bool |  |
| NoActivity48hTime | TimeSpan |  |
| MissingPhotoEnabled | bool |  |
| MissingPhotoTime | TimeSpan |  |
| AdminDailySummaryEnabled | bool |  |
| AdminDailySummaryTime | TimeSpan |  |
| AdminPhoneNumber | string? | recipient for AdminDailySummary |
| WorkingDaysOnly | bool | default true |
| LastTickAt | DateTime UTC | persisted last-run timestamp; survives deploys |

**`Employee.SmsRemindersEnabled`** — `bool default true`. New column; needs a migration **and** SQLite + PostgreSQL self-heal blocks.

### 3.3 File map (mirrors the skeleton in `CHAT_HANDOFF.md`)

```
API/
  Controllers/
    SmsRemindersController.cs        ← NEW. GET/PUT /api/sms-reminders/config,
                                       GET /api/sms-reminders/history?from=&to=,
                                       POST /api/sms-reminders/test { phone, body }
  Services/
    ISmsService.cs                   ← NEW. SendAsync(toE164, body, ct) → SmsSendResult
    TwilioSmsService.cs              ← NEW. Reads SMS_ACCOUNT_SID/SMS_AUTH_TOKEN/SMS_FROM
    SmsApiSkSmsService.cs            ← NEW (alt). Used if customer picks SMSAPI.sk
    NullSmsService.cs                ← NEW. Dev/local impl that just logs — keeps M0 dry runs cheap
    ReminderTriggerEvaluator.cs      ← NEW. Pure logic; unit-testable without a DB by injecting
                                       a small ITimeEntryQueries seam
  BackgroundServices/
    SmsReminderBackgroundService.cs  ← NEW. 60s tick. Pulls NotificationConfig, converts to
                                       Europe/Bratislava, fires matching triggers,
                                       writes SmsReminder rows.
  Models/
    SmsReminder.cs                   ← NEW
    NotificationConfig.cs            ← NEW
    Employee.cs                      ← MODIFIED — add SmsRemindersEnabled
  Migrations/
    <generated>_AddSmsReminders.cs   ← via `cd API && dotnet ef migrations add AddSmsReminders`
  Program.cs                         ← MODIFIED — register ISmsService, hosted service,
                                       add SQLite + PostgreSQL self-heal blocks for the new
                                       column / tables
  appsettings.json / appsettings.Development.json
                                     ← MODIFIED — Sms section with provider, fromNumber,
                                       defaults; secrets stay in env vars on Railway

client/src/app/
  pages/
    notifications/                   ← NEW admin-only page "Notifikácie"
      notifications.page.ts
      notifications.page.html
      notifications.page.css
  services/
    sms-reminder.service.ts          ← NEW — wraps the new endpoints
  app.routes.ts                      ← MODIFIED — add /admin/notifikacie route
  components/navbar/navbar.component.html
                                     ← MODIFIED — new "Notifikácie" link (desktop + mobile)
```

### 3.4 Why a `BackgroundService` and not Hangfire

- We have one tenant (one Slovak firm). Hangfire's dashboard, retry tables, and Postgres job storage would add operational complexity for ~5 daily triggers.
- A `BackgroundService` running a 60-second tick can: read config, compute the local time in Bratislava, fan out, write audit rows, and persist `LastTickAt`. That's ~50 lines.
- If we ever need fan-out across machines (multi-Railway-instance), revisit this — but Railway runs us on one container today.
- **Persistence guarantee**: `NotificationConfig.LastTickAt` is updated after every dispatch. If the container restarts mid-day, on restart we look at the last tick and decide whether each scheduled time falls in `(LastTickAt, now]`. This gives us "fire if missed, don't fire twice" semantics.

---

## 4. Trigger Logic (each in plain English)

All time math runs in `Europe/Bratislava`. Every trigger checks `Employee.IsActive && Employee.SmsRemindersEnabled && Employee.PhoneNumber != null`.

1. **ClockInNudge** — fires at `ClockInNudgeTime`, working days only (if configured). For each eligible employee with **no** TimeEntry whose `ClockIn` falls today, send the clock-in reminder. Idempotency key `(EmployeeId, ClockInNudge, today)`.

2. **ClockOutNudge** — fires at `ClockOutNudgeTime`. For each employee who **has** a TimeEntry today with `ClockIn != null && ClockOut == null`, remind them to clock out. Idempotency key `(EmployeeId, ClockOutNudge, today)`.

3. **NoActivity48h** — fires at `NoActivity48hTime`. For each employee with **zero** TimeEntries in the past 48h on a working day, send a "we haven't seen you" message. **Mute** the trigger for two days post-creation of a new employee (don't spam them on day 1). Idempotency key `(EmployeeId, NoActivity48h, today)`.

4. **MissingPhoto** — fires at `MissingPhotoTime`. For each TimeEntry today with `ClockOut != null && (PhotoUrl == null || PhotoUrl == "")`, remind that employee that the photo is still missing. Idempotency key `(EmployeeId, MissingPhoto, today)`.

5. **AdminDailySummary** — fires at `AdminDailySummaryTime`. Single SMS to `NotificationConfig.AdminPhoneNumber` listing names of employees who didn't clock today. Capped at 5 names + "+ N ďalších" if longer (SMS length budget). Idempotency key `(null, AdminDailySummary, today)`.

6. **AdminTest** — never fires automatically. Triggered by `POST /api/sms-reminders/test`. Always logged to `SmsReminder` for audit, but does not consume the daily idempotency slot.

---

## 5. Migration & Self-Heal Plan (CRITICAL — see PROJECT_NOTES.md §"Migration Safety Rules")

We will **never** hand-write the migration `.cs` file. Steps:

```
cd API
dotnet ef migrations add AddSmsReminders
# Inspect Up()/Down() — must only contain CreateTable + AddColumn,
# no destructive ops, no DELETE/TRUNCATE
dotnet run        # local SQLite must come up clean — zero `no such column` errors
```

Then add to `Program.cs` (at the same place the V1.1 self-heal blocks live, using the V1.1.1 `pragma_table_info` style — no try/catch noise):

```csharp
// SQLite self-heal — Employee.SmsRemindersEnabled
if (string.IsNullOrEmpty(databaseUrl))
{
    var hasCol = await db.Database
        .SqlQueryRaw<int>(@"SELECT COUNT(*) AS Value FROM pragma_table_info('Employees') WHERE name='SmsRemindersEnabled'")
        .FirstAsync();
    if (hasCol == 0)
        await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Employees"" ADD COLUMN ""SmsRemindersEnabled"" INTEGER NOT NULL DEFAULT 1");
}

// PostgreSQL self-heal — same column
// (use the existing DO $$ IF NOT EXISTS pattern already in Program.cs)
```

The two new tables (`SmsReminders`, `NotificationConfigs`) are created by the EF migration; the self-heal only needs to cover them defensively if we ever ship a hotfix that depends on them existing — keep the same `IF NOT EXISTS` style for both providers.

**Pre-deploy checklist (per Migration Safety Rules):**
- [ ] `dotnet ef migrations list` shows `AddSmsReminders` as pending locally
- [ ] `Up()` only uses `CreateTable` / `AddColumn` / `CreateIndex`
- [ ] `dotnet run` boots clean against local SQLite
- [ ] `dotnet run` boots clean against a Postgres replica of production (if available)
- [ ] No `DROP` / `RENAME` / `DELETE` / `TRUNCATE` anywhere in the migration

---

## 6. Slovak Copy Drafts (for customer review — Q5)

Keep each under 160 chars (1 SMS segment) where possible. Slovak diacritics are GSM-7 compatible.

| Trigger | Draft body | Char count |
|---|---|---|
| ClockInNudge | `Šichtovnica: Dobré ráno {meno}, nezabudni si zapísať príchod do práce.` | ~70 |
| ClockOutNudge | `Šichtovnica: {meno}, ešte si si nezapísal/a odchod. Zaeviduj koniec šichty, prosím.` | ~85 |
| NoActivity48h | `Šichtovnica: {meno}, posledné 2 dni nemáš žiadny záznam. Ak si v práci, zapíš si hodiny v aplikácii.` | ~105 |
| MissingPhoto | `Šichtovnica: {meno}, k dnešnej šichte ti chýba fotografia. Prosím doplň ju v aplikácii.` | ~90 |
| AdminDailySummary | `Šichtovnica: Dnes sa neprihlásili: {meno1}, {meno2}, {meno3}{+ X ďalších}.` | varies |
| AdminTest | `Šichtovnica TEST: ak vidíš túto správu, SMS funguje. Odoslané {HH:mm}.` | ~75 |

Notes: avoid GSM-7 unsupported chars (no curly quotes, em-dashes, or non-breaking spaces). Use `Šichtovnica:` as the consistent prefix so the worker recognises the source on a locked screen.

---

## 7. Phased Roadmap

Each phase is a deployable unit. **No phase ships until the previous is verified in production.**

### M0 — Customer alignment (NO CODE)
- Run the §1 questions list with the customer. Record answers in `PROJECT_NOTES.md`.
- Pick the SMS provider and create the account. Capture credentials into Railway env vars.
- Approve / edit the §6 Slovak copy.

### M1 — Infra (no user-visible reminders yet)
- Add `Employee.SmsRemindersEnabled`, `SmsReminders`, `NotificationConfigs` via EF migration + self-heal.
- Implement `ISmsService` + chosen provider impl + `NullSmsService` for local dev.
- Add `appsettings.json` `Sms` section; document the new env vars in a comment block (do not commit secrets).
- `Program.cs`: register hosted service but disable all triggers in seed config.
- Smoke test: hit `POST /api/sms-reminders/test` with a personal phone — verify a real SMS lands.

### M2 — First reminder (whichever the customer picked in Q1)
- Implement `ReminderTriggerEvaluator` for **one** trigger only.
- Wire it into `SmsReminderBackgroundService`.
- Verify idempotency by killing the container during a tick and confirming no duplicate sends.
- Verify Bratislava timezone correctness across DST (use a unit test that pretends `now = 2026-10-25 02:30 UTC`).

### M3 — Admin "Notifikácie" page
- New Angular page with one card per trigger (toggle + time picker), opt-out checkbox per employee, and a 30-day history table.
- Slovak labels per the existing UI conventions.
- "Test SMS" panel: phone-number field, free-text body, "Odoslať" button → calls `POST /api/sms-reminders/test`.
- PWA testing checklist (per `PROJECT_NOTES.md`): iPhone Safari standalone, dynamic island, etc.

### M4 — Remaining triggers
- Implement the other triggers from §4 one at a time, each shipping behind its own toggle so the customer can enable them gradually.

### M5 — Polish & retention
- Background sweeper that purges `SmsReminder` rows older than `retentionDays`.
- Slovak public-holiday support (if Q3 says yes) — we'd add a `SlovakHolidayCalendar` static list, refreshed yearly.
- Per-employee opt-out granularity (per-trigger), if customer asks.

---

## 8. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **Duplicate sends after deploy mid-day** | `LastTickAt` persistence + unique index on `(EmployeeId, Type, TriggerDate)` enforces "at most once per day per type". |
| **Wrong timezone math during DST switch** | Use `TimeZoneInfo.ConvertTimeFromUtc` with `Europe/Bratislava`; cover both DST transitions in unit tests. |
| **Cost overrun if a bug fires repeatedly** | Hard cap: `SmsReminderBackgroundService` short-circuits if more than `N=200` sends are queued in a single tick. Logged + alerted. |
| **GDPR — phone numbers in audit log** | 90-day default retention (configurable); document in customer's privacy notice that numbers are stored for delivery audit purposes. |
| **Worker churn — old phone receives messages** | When admin updates `Employee.PhoneNumber`, write the old value to a small audit field and stop sending to the old number. (Add to M5 if customer cares.) |
| **Provider outage** | Failed sends are logged with `Status=Failed` and `ErrorMessage`. Optional: retry once after 5 minutes. Do not block other employees on one failure. |
| **Worker can't read GSM-7 diacritics on old phones** | Modern Slovak carriers deliver UCS-2 transparently; cost doubles for messages > 70 chars in UCS-2. Test on a budget Android device before rollout. |
| **Customer changes copy after launch** | Copy lives in `NotificationConfig` columns or a small `SmsTemplates` table — never hard-code in C#. (Defer the table to M5 unless customer wants edit-in-UI from day one.) |
| **Production data loss via migration mistake** | Generated-only EF migrations + self-heal blocks + pre-deploy checklist (§5). |

---

## 9. Cost Sanity Check (back-of-envelope)

Assumptions: ~30 active workers, 22 working days/month, 1 reminder/worker/day average.

| Provider | SK rate (€/SMS) | Monthly est. (660 SMS) | Notes |
|---|---|---|---|
| Twilio | ~0.075 | €49 | Easiest C# SDK, generous free trial |
| SMSAPI.sk | ~0.030 | €20 | Slovak-native, REST API, requires sender-name registration with regulator |
| smsmanager.cz | ~0.025 | €17 | Czech provider, supports SK numbers; SK-language support |

Recommend **Twilio for M1/M2** to ship fast, then re-evaluate cost in M5.

---

## 10. Testing Checklist

### Unit
- `ReminderTriggerEvaluator` — feed synthetic `TimeEntry` lists, assert correct fan-out per trigger.
- DST transitions (`Europe/Bratislava` 02:00 spring forward, 03:00 fall back).
- Idempotency: same trigger run twice in the same tick → second run produces zero sends.

### Integration
- Boot API against local SQLite → tables created, no `no such column` errors.
- Boot API against a Postgres replica → migration + self-heal both clean.
- `POST /api/sms-reminders/test` → real SMS lands on tester's personal phone.

### End-to-end (PWA, per `PROJECT_NOTES.md` §"Testing checklist")
- Notifikácie page on Chrome desktop, iPhone Safari standalone (most important), iPad Safari landscape, Android Chrome installed.
- Date pickers and time pickers honour the iOS 16px input rule (no zoom-on-focus).
- Sticky header / footer respect safe-area insets.

### Manual operations
- Toggle a trigger off mid-day → tick after the next minute does not send.
- Toggle a trigger on → next configured time fires exactly once.
- Set an employee's `SmsRemindersEnabled = false` → they're skipped in evaluator.
- Kill the container 30s after a tick fires → on restart, no duplicate sends.

---

## 11. Out of Scope (V2+, do not build now)

- Push notifications via PWA service worker (BACKLOG.md "General notifications research") — separate decision.
- Email-to-SMS gateway (BACKLOG.md "SMS via universal address") — research item, not a shipping plan.
- Two-way SMS (worker replies "OK" → marks shift confirmed). Adds webhook + carrier complexity.
- Multi-firm tenanting. Single-firm assumption is baked into `NotificationConfig` being a one-row table.
- A/B testing of reminder copy.

---

## 12. Definition of Done (M2 — first shipped reminder)

- [ ] Customer signed off on copy + schedule for the chosen trigger.
- [ ] EF migration generated via CLI; `.cs` and `.Designer.cs` both committed.
- [ ] SQLite + PostgreSQL self-heal blocks added in `Program.cs`.
- [ ] `dotnet run` boots clean, no `fail:` log lines on startup.
- [ ] `POST /api/sms-reminders/test` works end-to-end against the chosen provider.
- [ ] One real reminder has fired in production for one real worker, and the audit row is correct.
- [ ] `CHAT_HANDOFF.md` updated with the new state and any deferred items.
- [ ] `BACKLOG.md` checkbox flipped for the implemented trigger.

---

*End of plan. Re-read CHAT_HANDOFF.md and PROJECT_NOTES.md before starting M1.*
