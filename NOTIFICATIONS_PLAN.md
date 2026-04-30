<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Notifications Plan — Push + WhatsApp (V1)

> Author: planning chat, 2026-04-26
> Status: **M1 SHIPPED (code complete, awaiting customer test) — 2026-04-26**
> Supersedes: `SMS_PLAN.md` (kept for reference; SMS deferred to V2 if push + WhatsApp prove insufficient)
> Companion docs: `CHAT_HANDOFF.md`, `PROJECT_NOTES.md`, `BACKLOG.md`

---

## ⚡ Shipped state — 2026-04-26

**M1 (push + admin UI + demo controls) is code-complete.** Frontend `tsc --noEmit` passes clean.

**Backend (ASP.NET Core 9):**
- Models: `PushSubscription`, `NotificationLog`, `NotificationConfig`; `Employee` extended with `NotificationsEnabled`, `WhatsAppEnabled`, `WhatsAppNumber`.
- Services: `WebPushService` (lib-net-webpush), `WhatsAppCloudApiService` (stub for M3), `NoActivity48hEvaluator`.
- `NotificationBackgroundService` — 60s tick, fires at the configured `Europe/Bratislava` time, idempotent via `(EmployeeId, Channel, TriggerType, TriggerDate)` unique index.
- `NotificationsController` — 13 endpoints incl. `vapid-public-key`, `subscribe`, `unsubscribe`, `config`, `employees`, `history`, `test/push`, `test/whatsapp`, `fire-now`, `fire-for-employee` (with `ignoreIdempotency`), `reset-today`.
- Migration `20260426150000_AddNotifications` with SQLite + PostgreSQL self-heal blocks in `Program.cs`.

**Frontend (Angular 20 standalone + Tailwind):**
- `client/public/sw.js` — push + notificationclick handlers.
- `client/src/app/services/push.service.ts` — VAPID subscribe flow.
- `client/src/app/services/notification-config.service.ts` — typed HTTP client.
- `client/src/app/pages/notifications/` — admin Notifikácie page with 4 cards: Konfigurácia, Zamestnanci, Test & Ukážka, História.

**Demo controls (per the user's "I can test it on prod / show it to the customer" requirement):**
- Test push (single employee, custom title/body)
- Test WhatsApp (single employee, real send)
- "Fire now" — runs the 48h evaluator immediately for everyone
- "Fire for employee" with `ignoreIdempotency: true` — resends to a chosen worker on demand
- "Reset today's logs" — clears today's `NotificationLog` rows so demos can be re-run

**Known follow-ups (not blocking M1):**
- M2: ServiceWorker registration trigger from the Kiosk login flow (needs UX call — banner vs. silent).
- M3: WhatsApp template approval + Meta credential plumbing in `appsettings.Production.json`.
- VAPID keys must be generated and put into `appsettings.json` (`Notifications:Vapid:PublicKey`, `:PrivateKey`, `:Subject`) before first deploy.
- Backend `dotnet build` not run in this sandbox — verify locally on Windows before publish.



---

## 1. Scope (deliberately narrow)

> The customer's workforce is **older, non-tech-savvy** construction workers. Anything we build must be obvious, big, in plain Slovak, and must not require any tech literacy beyond "tap the icon".

**One trigger, one purpose, two free channels.**

- **Trigger:** worker has not clocked any hours in the **past 2 days** (`NoActivity48h`).
- **Channels (in priority order):**
  1. **PWA push notification** — primary. Free, works on Android Chrome and iOS 16.4+ Safari (when added to home screen).
  2. **WhatsApp Business Cloud API** — secondary. Used only for employees who have WhatsApp **and** opt in (manager flips a per-employee toggle). Free tier: 1000 service conversations/month from Meta is plenty for ~30 workers.
- **No SMS in V1.** Re-enable later if push + WhatsApp coverage is incomplete (see `SMS_PLAN.md` for that path).

**Out of scope for V1** — clock-in nudge, clock-out nudge, missing-photo nudge, admin daily summary, two-way replies, Telegram, Viber. All of those wait until the 48h-no-activity reminder is shipped, used, and confirmed working.

---

## 2. Open Questions for the Customer (must be answered first)

| # | Question | Default if customer is unsure |
|---|---|---|
| Q1 | Confirm the trigger: "remind a worker if they have not logged any hours in the past 2 days" — correct? | Yes |
| Q2 | What time of day should the reminder fire? `Europe/Bratislava`. | 18:00 (after typical end-of-shift, before evening) |
| Q3 | Working days only (Mon–Fri), or every day? | Mon–Fri only — avoid weekend pestering |
| Q4 | Do all employees use WhatsApp, some, or none? Need a yes/no list per employee. | Manager fills in `WhatsAppEnabled` toggle per employee in admin panel |
| Q5 | Confirm exact Slovak copy (drafts in §6). Older-worker readability matters. | Drafts approved as-is unless customer changes |
| Q6 | Should the manager be CC'd when a worker doesn't clock for 2 days? | Yes, single daily summary push to the manager only |
| Q7 | Is the customer willing to set up a Meta Business / WhatsApp Business account (one-time), or skip WhatsApp entirely and start with push only? | Push-only first (M1). WhatsApp added in M3 only if customer agrees to set up Meta Business. |
| Q8 | Where should the existing iOS / Android home-screen guides live in the app? Workers need to install the PWA before push works. | Add a "Ako povoliť upozornenia" link on the kiosk start screen, opening the relevant existing PDF (`Sichtovnica_iOS_Sprievodca.pdf` / `Sichtovnica_Android_Sprievodca.pdf`). |

**Action: 15-min call to clear Q1–Q4 and Q7. Q5 copy can be iterated.**

---

## 3. Why This Approach (older-worker rationale)

- **Push is free and silent-by-default-friendly.** No bills, no provider account, no template approval. Lock-screen banner with the company logo and a one-line message in Slovak — that's it.
- **WhatsApp matches older Slovak users' habits.** Many people in this demographic use WhatsApp daily for family. They're more likely to read a WhatsApp than tap an unknown SMS sender.
- **PWA is already installed in many cases.** The customer has been distributing the iOS / Android install guides (`Sichtovnica_*_Sprievodca.pdf`). Push reuses that work — no new install step beyond granting the notification permission.
- **One reminder, not many.** Older workers will quickly disengage if they get spam. One nudge per 48h of silence respects their attention.
- **Big visible "Povoliť upozornenia" button.** A worker who is not sure what push notifications are can tap a single big button on the kiosk, see the system permission dialog in Slovak, and tap "Povoliť". No menus, no settings page hunt.

---

## 4. Architecture

```
                        ┌───────────────────────────────────────────┐
                        │  NotificationBackgroundService (1-min tick)│
                        │  - reads NotificationConfig (single row)   │
                        │  - converts now → Europe/Bratislava        │
                        │  - if today is working day & time matches: │
                        │      run NoActivity48h evaluator           │
                        └────────────────┬───────────────────────────┘
                                         │
                                         ▼
        ┌────────────────────────────────────────────────────────────┐
        │  NoActivity48hEvaluator                                    │
        │  - for each Employee where IsActive && NotificationsEnabled│
        │  - if no TimeEntry in past 48h on a working day            │
        │  - check idempotency (NotificationLog: one send per day)   │
        │  - returns list<(employee, body)>                          │
        └────────────────┬─────────────────────────┬─────────────────┘
                         │                         │
              push channel│              whatsapp channel (if employee.WhatsAppEnabled)
                         ▼                         ▼
        ┌──────────────────────────┐    ┌─────────────────────────┐
        │ IPushNotificationService │    │ IWhatsAppService        │
        │  └── WebPushService      │    │  └── WhatsAppCloudApi   │
        │   (lib-net-webpush, VAPID)│    │   (Meta Cloud API HTTP)│
        └────────────┬─────────────┘    └────────────┬────────────┘
                     │                               │
                     ▼                               ▼
                ┌─────────────────────────────────────────┐
                │   NotificationLog row written:           │
                │   EmployeeId, Channel, Body, SentAt,     │
                │   Status, ProviderMessageId, ErrorMessage│
                └─────────────────────────────────────────┘
```

### 4.1 Data model

**`PushSubscription`** — one row per device that opted in. A worker may have two (phone + tablet).
| Column | Type | Notes |
|---|---|---|
| Id | int PK |  |
| EmployeeId | int FK → Employees |  |
| Endpoint | string (unique) | from the browser's PushSubscription |
| P256dhKey | string |  |
| AuthKey | string |  |
| UserAgent | string? | for diagnostics |
| CreatedAt | DateTime UTC |  |
| LastUsedAt | DateTime UTC |  |

**`NotificationLog`** — append-only audit of every send across both channels.
| Column | Type | Notes |
|---|---|---|
| Id | int PK |  |
| EmployeeId | int FK → Employees | nullable for manager-summary sends |
| Channel | string | `Push`, `WhatsApp` |
| TriggerType | string | `NoActivity48h`, `ManagerSummary`, `Test` |
| Body | string |  |
| TriggerDate | date | basis of idempotency |
| SentAt | DateTime UTC |  |
| Status | string | `Sent`, `Failed`, `Skipped`, `NoSubscription` |
| ProviderMessageId | string? |  |
| ErrorMessage | string? |  |

Unique index: `(EmployeeId, Channel, TriggerType, TriggerDate)`.

**`NotificationConfig`** — single-row table.
| Column | Type | Notes |
|---|---|---|
| Id | int PK | always 1 |
| NoActivity48hEnabled | bool | master switch |
| NoActivity48hTime | TimeSpan | local Bratislava |
| WorkingDaysOnly | bool | default true |
| ManagerSummaryEnabled | bool |  |
| ManagerSummaryEmployeeId | int? | the employee record that represents the manager (gets the daily summary push) |
| LastTickAt | DateTime UTC | survives container restarts |

**`Employee` — three new columns**
| Column | Type | Default |
|---|---|---|
| NotificationsEnabled | bool | true |
| WhatsAppEnabled | bool | false (opt-in) |
| WhatsAppNumber | string? | nullable; falls back to PhoneNumber if null and WhatsAppEnabled |

All schema changes go through `dotnet ef migrations add AddNotifications` + SQLite + PostgreSQL self-heal blocks (see §6).

### 4.2 File map

```
API/
  Controllers/
    NotificationsController.cs        ← NEW
      GET  /api/notifications/config            (admin)
      PUT  /api/notifications/config            (admin)
      GET  /api/notifications/history?from=&to= (admin)
      POST /api/notifications/test/push         (admin — sends a test push to one employee)
      POST /api/notifications/test/whatsapp     (admin — sends a test WhatsApp to one employee)
      POST /api/notifications/subscribe         (kiosk — body: { employeeId, pin, subscription })
      DELETE /api/notifications/subscribe       (kiosk — unsubscribe)
      GET  /api/notifications/vapid-public-key  (kiosk — needed by service worker)
  Services/
    IPushNotificationService.cs       ← NEW
    WebPushService.cs                 ← NEW (lib-net-webpush NuGet package)
    IWhatsAppService.cs               ← NEW
    WhatsAppCloudApiService.cs        ← NEW (HttpClient to graph.facebook.com)
    NoActivity48hEvaluator.cs         ← NEW (pure logic, unit-testable)
  BackgroundServices/
    NotificationBackgroundService.cs  ← NEW (60s tick)
  Models/
    PushSubscription.cs               ← NEW
    NotificationLog.cs                ← NEW
    NotificationConfig.cs             ← NEW
    Employee.cs                       ← MODIFIED (3 new columns)
  Migrations/
    <generated>_AddNotifications.cs   ← via dotnet ef migrations add
  Program.cs                          ← MODIFIED (DI registration + self-heal blocks)
  appsettings.json                    ← MODIFIED (Notifications section + VAPID + WA placeholders)

client/
  src/
    sw.js                             ← NEW or extended (push event handler + click handler)
    app/
      pages/
        kiosk/
          kiosk.page.html             ← MODIFIED — big "Povoliť upozornenia" tile
          kiosk.page.ts               ← MODIFIED — subscribe flow, link to install guide PDFs
        notifications/                ← NEW admin page
          notifications.page.ts
          notifications.page.html
      services/
        push.service.ts               ← NEW (wraps SwPush + subscribe API)
        notification-config.service.ts← NEW
      app.routes.ts                   ← MODIFIED — /admin/notifikacie
      components/navbar/navbar.component.html
                                      ← MODIFIED — "Notifikácie" admin link
```

### 4.3 Why a `BackgroundService` and not Hangfire

Same rationale as `SMS_PLAN.md` §3.4 — single tenant, one trigger, ~50 lines of code. `LastTickAt` persistence + the unique idempotency index gives "fire if missed once, never twice" semantics across container restarts.

---

## 5. Trigger Logic (only one)

`NoActivity48h` — fires at `NotificationConfig.NoActivity48hTime` on working days (if `WorkingDaysOnly`).

```
let now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, "Europe/Bratislava")
let twoDaysAgo = now.Date.AddDays(-2)

for each Employee e where e.IsActive && e.NotificationsEnabled:
    let recentEntries = TimeEntries
        where EmployeeId == e.Id
        and ClockIn >= twoDaysAgo
    if recentEntries is empty:
        if not already sent today (NotificationLog idempotency):
            if e has any PushSubscription rows:
                push: title="Nezabudni na hodiny", body=copyForEmployee(e)
            if e.WhatsAppEnabled and (e.WhatsAppNumber || e.PhoneNumber) is set:
                whatsapp: utility-template send
            log every attempt (Sent / Failed / NoSubscription / Skipped) per channel
```

**Mute** for the first 3 days after `Employee.CreatedAt` (don't pester a brand-new hire).

**Manager summary** — single push to `ManagerSummaryEmployeeId`'s subscriptions if any worker triggered today. Body: `"Šichtovnica: Dnes 2+ dni bez záznamu: {meno1}, {meno2}{+ X ďalších}."`

---

## 6. Migration & Self-Heal Plan

Same rules as `PROJECT_NOTES.md` §"Migration Safety Rules" and `SMS_PLAN.md` §5. Always:

```
cd API
dotnet ef migrations add AddNotifications
# inspect Up()/Down() — only CreateTable / AddColumn / CreateIndex
dotnet run    # local SQLite must boot clean
```

Self-heal blocks for each new column on `Employees` (using V1.1.1 `pragma_table_info` style for SQLite, `information_schema.columns` for PostgreSQL):
- `Employees.NotificationsEnabled` (bool, default true)
- `Employees.WhatsAppEnabled` (bool, default false)
- `Employees.WhatsAppNumber` (string, null)

New tables (`PushSubscriptions`, `NotificationLogs`, `NotificationConfigs`) created by the EF migration. Self-heal `IF NOT EXISTS` block as a backstop.

---

## 7. Slovak Copy Drafts (older-worker friendly)

> Principle: **Use simple words, no jargon, no abbreviations, no emojis.** Worker should understand the message in 2 seconds even with reading glasses.

**Push notification — worker**
- Title: `Nezabudni na hodiny`
- Body: `Posledné 2 dni nemáš zapísané hodiny v aplikácii Šichtovnica. Otvor aplikáciu a doplň ich, prosím.`
- Click action: opens kiosk start page

**Push notification — manager (daily summary)**
- Title: `Šichtovnica — denný prehľad`
- Body: `Dnes 2 dni bez záznamu: {meno1}, {meno2}{+ X ďalších}.`
- Click action: opens admin "Záznamy dochádzky" page filtered to today

**WhatsApp message — worker (Utility template, must be approved by Meta)**
```
Dobrý deň {meno}, posledné 2 dni nemáš zapísané hodiny v aplikácii Šichtovnica.
Otvoríš ju, prosím, a doplníš svoje hodiny? Ďakujeme.

— Šichtovnica
```

WhatsApp utility templates allow placeholder variables `{{1}}` (= meno). Approval typically takes a few hours via Meta Business Manager.

**Test send (admin button)**
- Push title: `Šichtovnica TEST`
- Push body: `Toto je testovacie upozornenie. Ak ho vidíš, push funguje. {HH:mm}`
- WhatsApp: same text, no template needed (free-form messages allowed within a 24h customer-service window after the recipient messages the bot first).

---

## 8. Phased Roadmap

### M0 — Customer alignment (NO CODE)
- Run §2 questions. Record answers in `PROJECT_NOTES.md`.
- Confirm: push-first or push+WhatsApp simultaneously.
- Generate VAPID keypair (one-time): `npx web-push generate-vapid-keys`. Store private key in Railway env var `VAPID_PRIVATE_KEY`, public in `VAPID_PUBLIC_KEY`.
- If WhatsApp: customer creates Meta Business account, verifies a number, drafts the utility template.

### M1 — Push notifications, single trigger
- EF migration + self-heal for the 3 new Employee columns and 3 new tables.
- `IPushNotificationService` + `WebPushService` using `WebPush` NuGet (the `lib-net-webpush` port).
- Service worker push handler + click handler in `client/src/sw.js`.
- Kiosk: prominent "Povoliť upozornenia" tile on the start screen — big green button, big text, with an "Ako to funguje?" link to the existing install-guide PDFs.
- `POST /api/notifications/subscribe` stores subscription per employee (PIN-gated).
- `NotificationBackgroundService` runs at the configured time on working days; sends push to subscribed workers who have been silent 48h.
- Unit tests: `NoActivity48hEvaluator` against synthetic time entries; DST math.
- E2E test on iPhone Safari standalone, Android Chrome installed.

### M2 — Admin "Notifikácie" page
- Toggle the trigger on/off, set fire time, set working-days-only.
- Per-employee table: `NotificationsEnabled` checkbox, `WhatsAppEnabled` checkbox, last-notified date.
- 30-day history table.
- Test buttons: "Poslať testovacie push" + (in M3) "Poslať testovaciu WhatsApp".

### M3 — WhatsApp channel (only if customer set up Meta Business in M0)
- `IWhatsAppService` + `WhatsAppCloudApiService` calling `https://graph.facebook.com/v.../{phone_number_id}/messages`.
- Read `WHATSAPP_TOKEN`, `WHATSAPP_PHONE_NUMBER_ID`, `WHATSAPP_TEMPLATE_NAME` from env.
- Evaluator fan-out updated to also send WhatsApp when `Employee.WhatsAppEnabled`.
- "Test WhatsApp" admin button.

### M4 — Polish
- Manager daily summary push.
- Retention sweeper for `NotificationLog` (default 90 days).
- Slovak public-holiday calendar (skip reminders on `Veľkonočný pondelok`, `1.5`, `1.9`, etc.).

---

## 9. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **Older worker doesn't grant permission** | Big "Povoliť upozornenia" tile on kiosk start. Link to existing install-guide PDFs. Manager can also walk them through it on shift change. |
| **PWA not installed on home screen → no push on iOS** | iOS 16.4+ requires Add-to-Home-Screen for push. Show an "iOS: pridaj do plochy" reminder if `window.matchMedia('(display-mode: standalone)').matches === false` on iOS Safari. |
| **Worker uninstalls PWA → silently stops receiving push** | Server-side: when push send returns `410 Gone` from the push service, mark the subscription stale and (optionally) flip a `Employee.PushHealthy = false` flag the manager can see in the Notifikácie page. |
| **WhatsApp template not approved by Meta** | Submit early, in M0. Have a backup plain-language template ready. |
| **WhatsApp account suspended for spam** | Only send to opted-in employees. Use a Utility template, not Marketing. Throttle to ≤ 1 message per worker per 48h. |
| **Worker has neither push nor WhatsApp** | Manager sees this in the Notifikácie page (last-notified date is empty). Falls back to phone call until SMS V2. |
| **Bratislava DST math wrong** | `TimeZoneInfo.ConvertTimeFromUtc("Europe/Bratislava")` — same approach as `KioskController`. Add unit tests for both DST transitions. |
| **Duplicate sends across container restarts** | Unique index on `(EmployeeId, Channel, TriggerType, TriggerDate)` + `LastTickAt` persistence. |
| **Kiosk worker accidentally subscribes another worker's device** | `POST /api/notifications/subscribe` requires the worker's PIN, same as kiosk clock-in. Subscription is bound to the employee whose PIN matched. |
| **Web push secret leak** | VAPID private key stored only in Railway env. Frontend only ever sees the public key from `GET /api/notifications/vapid-public-key`. |

---

## 10. Older-Worker UX Considerations (CRITICAL)

This section codifies the "remember: these are older workers" requirement. Any UI added for V1 must respect it.

- **Big targets.** "Povoliť upozornenia" button at least 64px tall with 18–20px font.
- **Plain Slovak.** Never use words like "push", "subscription", "VAPID", "notification permission". Always say "upozornenie".
- **No tech jargon in error states.** Replace "Subscription failed: 503" with "Niečo sa pokazilo. Skús to ešte raz alebo zavolaj manažérovi."
- **No animation flourishes.** A pulsing button or sliding modal will distract or confuse. Steady, plain UI.
- **No multi-step flows.** Tap one button → see the system Slovak permission dialog → done. If the worker dismisses the system dialog, show a friendly "Zatial nedostaneš upozornenia. Skús znovu kedykoľvek." with the same button visible.
- **High contrast.** Stick to the existing Šichtovnica colour palette but ensure 4.5:1 contrast on the new elements.
- **Permanent visible state.** The "Povoliť upozornenia" button stays on the kiosk until the worker has subscribed, then becomes a small "Upozornenia zapnuté ✓" badge. No hiding it in a settings menu.
- **Test on a real older user.** Ask the customer to have one worker over 50 try the flow before rollout — this is the only meaningful test.
- **Pair with the existing PDF guides.** `Sichtovnica_iOS_Sprievodca.pdf` and `Sichtovnica_Android_Sprievodca.pdf` already exist and are workshop-tested. Link them prominently from the new "Povoliť upozornenia" tile.

---

## 11. Definition of Done (M1)

- [ ] Customer signed off on §2 + §7 copy.
- [ ] EF migration generated via CLI; `.cs` and `.Designer.cs` both committed.
- [ ] SQLite + PostgreSQL self-heal blocks added for the 3 new Employee columns.
- [ ] `dotnet run` boots clean — no `fail:` log lines on startup.
- [ ] VAPID keypair stored in Railway env.
- [ ] Test push delivered to manager's iPhone Safari (standalone) successfully.
- [ ] Test push delivered to a worker's Android Chrome (installed) successfully.
- [ ] One real 48h reminder has fired for one real worker, audit row correct.
- [ ] Older-worker walk-through: at least one worker over 50 has installed the PWA, granted permission, and confirmed they received the test push.
- [ ] `CHAT_HANDOFF.md` and `PROJECT_NOTES.md` updated with shipped state.
- [ ] `BACKLOG.md` "48-hour reminder system" checkbox flipped.

---

## 12. Open Items Parked for V2

- SMS as a fallback channel (see `SMS_PLAN.md`).
- Telegram / Viber bots — only if push + WhatsApp coverage is unacceptable.
- Two-way replies ("OK" → marks the employee as still active, suppresses tomorrow's reminder).
- Per-employee per-channel preference UI (today: WhatsApp is opt-in via a single toggle).
- Push delivery health dashboard.

---

*End of plan. Re-read CHAT_HANDOFF.md and PROJECT_NOTES.md before starting M1.*
