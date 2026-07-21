<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!",
no enthusiastic openings, no padding sentences. When in doubt, write less.
-->

# NEXT_CHAT_CONTEXT.md — handoff for a fresh session (2026-07-19)

Šichtovnica/Dochádzkovník — .NET 9 API + Angular 20 client (signals, @if/@for,
Tailwind v4, dark mode) for construction company **AZ Profistav, s.r.o.**
(IČO 47208368 — always the CUSTOMER on documents, never the supplier).
Slovak UI. Invoice scanning: Claude Sonnet primary, Gemini fallback.
Plan: sell the app white-label — 2 more customers lined up, each with own branding.

## Environments & workflow (UPDATED 2026-07-19)

- Branches: `dev` (Vercel preview + Railway dev API `dochadzkovnik-dev.up.railway.app`),
  `master` (production). Push to a branch auto-deploys both.
- **The agent MAY commit, push and deploy to dev** (user authorized 2026-07-18;
  supersedes the old "user pushes themselves" rule — that was a Linux-FUSE-sandbox
  problem, sessions now run on native Windows). Production merges need the user's OK.
- Connected tooling: git push via Windows Credential Manager; `gh` CLI (Throndil);
  Railway CLI (urbanek.m08@gmail.com) linked to project `distinguished-blessing`,
  env `dev`, service `Dochadzkovnik` — `railway deployment list --json`, `railway logs`.
  Do NOT link project `michal-urbanek` (personal site). Vercel MCP connector:
  team `team_Y782SEs8fNnovdGKrbzBU1G0`, project `dochadzkovnik`.
  Ignore the Railway CLI banner about `railway setup agent`.
- Local dev: docker compose postgres (user `dochadzkovnik`), API `dotnet run` on
  :5122, client ng serve on **:4400** (NOT 4200 — Windows reserved 4125-4324 after
  a reboot; `.claude/launch.json` is configured). Login for testing: superadmin
  `admin` / `SuperAdmin123!!` (from appsettings.Local.json; `vladosroka` has a
  security PIN the agent does not know).
- Verification loop: `dotnet test` in API.Tests (27 green), `npx tsc --noEmit -p
  tsconfig.app.json` in client, `npx ng build` before deploys, browser pane for
  functional checks. Restart the API server before `dotnet build` (file locks).

## State: CUSTOMER_ROADMAP_2026-07.md is DONE

Everything shipped and verified on dev; the roadmap file has per-row notes.
Highlights from 2026-07-18/19 (commits 751e106..a35a6f6):

- Fáza D complete (divisions, direction, burger, Súhrn, D6 monthly Excel report).
- F4 car/machine cost ledgers, F5 výjazdy (unique car+day, rate from Odvody),
  F6 fuel cards page /admin/palivove-karty + Employee.Position, F7 = no-op
  (Commander invoices are regular invoices).
- W1 hrubá sadzba (WageAtTime + Odvody "€/h" rows; payroll payouts unchanged),
  W2 was already done, W3 print pay slips (Tlačiť pásky on Mzdy).
- P1 Zložka podľa dní on location detail (week/day/range, per-day shifts,
  diary, material, documents, photos).
- AI usage window ("AI" button on invoices: month + total Claude spend).
- Bugfixes: parser VAT-summary thousand-grouping (A-Z STAV), payroll D8 filter
  (empty division counts as profistav).
- **Planner** (/admin/planner) behind `Planner` feature flag (seeded OFF):
  PlanEntry table (praca+LocationId | dovolenka/pn/volno, inclusive ranges),
  week grid with lane stacking, cross-week clipping, drag-select via pointer
  events (mouse only; touch taps). Kiosk deliberately not included yet.
- **PR #22 (dev -> master) is open and contains all of it.** Merging deploys
  prod; that is the user's call.

## Redesign (in progress — "Vzduch" direction)

- User picked light/airy "Vzduch" style; customer + workers prefer DARK MODE,
  so dark is the primary target. Implemented as a token layer in
  `client/src/styles.css`: slate ramp remapped to warm neutrals, amber ramp
  = brand variables (ten values), Figtree body + Bricolage Grotesque headings.
  Templates untouched; navigation/buttons/routing identical (hard rule).
- White-label: rebrand = override the ten `--color-amber-*` values + logo.
  Example ramps documented in styles.css (AZ amber, trust blue, teal).
- Kiosk keeps the old look: overrides scoped to `body:not(:has(.kiosk-root))`.
- NEXT STEP (not started): per-page polish pass from the approved mockup —
  greeting header ("Dobré ráno, <meno>"), tinted icon chips on KPI cards,
  colored pracovisko tags, squircle avatars. Top pages first: Prehľad,
  Financie, Mzdy. Mockup reference lived in the session scratchpad
  (redesign-mockups.html, served on :4390) — regenerate if needed: Smer 1
  "Vzduch" from the 2026-07-19 conversation.

## New tools: design skills installed in ~/.claude/skills (auto-load)

frontend-design (Anthropic official), web-design-guidelines (Vercel audit
rules), taste-skill + redesign-skill, impeccable, emil-design-eng +
improve-animations + animation-vocabulary, ui-ux-pro-max suite (design,
design-system, ui-styling, brand, banner-design, slides). Use frontend-design
+ redesign-skill for any UI work; ui-ux-pro-max/data/colors.csv has validated
palettes. Key lessons already applied: no purple/blue AI gradients, one
desaturated accent, warm neutral family, tabular-nums for money.

## Open items / parked

- P4 (dodatočné priraďovanie) deferred; Z2 device admin code; Z3 chatbot idea.
- D7 partial: pracovisko "Firma" and "Servis auta" remap waits on customer
  (see BACKLOG "Firma" clarification). Old service docs re-file manually via
  invoice review.
- Employee in BOTH divisions: parked question for next customer call.
- Výjazdy + hrubá sadzba read LIVE Odvody rates (ponytail comments in code);
  snapshot per TimeEntry if the customer ever complains about history shifts.
- PROJECT_NOTES Migration Safety Rule 3 (self-heal blocks) diverges from
  practice since the division migrations; either add blocks or amend the rule.
- MailKit NU1902 warning and heic2any CommonJS warning are known noise.
- Planner flag is OFF everywhere; flip in Account -> Funkcie to demo.

## Key files

- Roadmap + status notes: CUSTOMER_ROADMAP_2026-07.md
- Theme tokens: client/src/styles.css (top block)
- Planner: API/Controllers/PlannerController.cs, API/Models/PlanEntry.cs,
  client/src/app/pages/planner/*
- P&L math (výjazdy, hrubá): API/Controllers/LocationsController.cs
  BuildPnlDtoAsync
- Division report: API/Services/DivisionMonthlyReportBuilder.cs
- Parser: API/Services/InvoiceParser.cs; tests API.Tests/ (27)
