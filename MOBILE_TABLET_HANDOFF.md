<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no padding. Bold sparingly.
-->

# Mobile / Tablet UX — handoff for the next session

> Created 2026-05-01 at the end of the Commander integration session.
> Goal: make the app look and feel right on the tablet the customer is actually using.

## Why this exists

The customer uses the app primarily on a tablet. The session that just ended shipped the Commander integration, including a new `/admin/commander` page. That page was laid out desktop-first; it needs a tablet pass, and so do any other surfaces that feel cramped or untappable on the device.

## Target device

**Acer Iconia Tab A16** (model A16-11MN-A9VM). 11" IPS display, 8 GB RAM, 256 GB storage. Android. The customer uses it primarily in landscape on a stand, but portrait must work too.

Effective CSS viewport at default DPR is roughly 1366×1024 in landscape, 1024×1366 in portrait. The device sits right on Tailwind's `lg` breakpoint (1024 px), so the same rule that helps a desktop in 1024-mode helps the tablet in landscape, and the same rule that helps a phone in portrait helps the tablet in portrait. Test both orientations.

Reference: https://www.nay.sk/bazar/tablet-acer-iconia-a16-a16-11mn-a9vm-8-gb-256-gb-nt-lkaee-008-strieborny-zanovny-24-mesiacov-zaruka

## Read first, in this order

1. `PROJECT_NOTES.md` → section "📱 PWA / Mobile Compatibility (READ THIS FIRST FOR ANY UI WORK)". The PWA, safe-area, dvh, no-zoom-on-input, and iOS rules are already in place. Don't break them.
2. `CHAT_HANDOFF.md` → top of file for the latest deploy state.
3. This file.

## What just shipped (so you know which surfaces are new)

Commander integration, M1 + M2, all behind the `CommanderIntegration` feature flag (toggle in Account → Funkcie):

- Backend: read-only typed `HttpClient` over the customer's Commander v1 fleet API. Basic auth, 429 + `Retry-After` honoured, `IMemoryCache` for `/vehicles` (24 h) and `/last-positions` (30 s), `/ride-summary` and `/rides` (60 s). Files:
  - `API/Services/ICommanderClient.cs`, `CommanderClient.cs`
  - `API/Controllers/CommanderController.cs`
  - `API/DTOs/CommanderDtos.cs`
  - `API/Converters/CommanderJsonConverters.cs`
  - `API/Program.cs` (added `CommanderIntegration` to `knownFlags`, `AddMemoryCache`, the typed `AddHttpClient` registration)
- No DB schema change. No EF migration.
- Frontend: new admin page `/admin/commander` (Detail and Prehľad tabs), plus a `CommanderCarPanelComponent` embedded on `/admin/cars/:id`. Files:
  - `client/src/app/pages/commander/commander.page.ts/html`
  - `client/src/app/components/commander-car-panel/commander-car-panel.component.ts/html`
  - `client/src/app/services/commander.service.ts`
  - `client/src/app/services/feature-flag.service.ts` (added `commanderIntegration` signal)
  - `client/src/app/guards/commander-feature.guard.ts`
  - `client/src/app/app.routes.ts` (new route)
  - `client/src/app/components/navbar/navbar.component.html` (new nav link, gated by flag-or-superadmin)
  - `client/src/app/pages/account/account.page.html` (toggle in Funkcie card)
  - `client/src/styles.css` (Leaflet CSS import + `.commander-marker` / `.cm-pin-*` / `.commander-ride-tooltip`)
  - `client/package.json` (added `leaflet@^1.9.4` and `@types/leaflet@^1.9.12`)

The map is now Leaflet on OSM tiles. There is no iframe. State machine: live mode (one red dot, follows `/last-positions` data) vs ride mode (green Štart pill, red Cieľ pill, amber dashed connector, fitBounds). Switching modes is driven by the `selectedRideId` signal. The map is a single Leaflet instance reused across both tabs by tearing down and re-mounting on tab switch.

## Likely tablet pain points (educated guesses; verify on the actual device)

In rough priority order:

1. `/admin/commander` Detail tab uses `grid-cols-1 lg:grid-cols-[18rem_1fr]`. In portrait on the Acer (~1024 wide) the layout collapses to single column, sidebar above detail. The sidebar's `max-h-[28rem] lg:max-h-[36rem]` becomes a tiny scroll area on a tall portrait viewport. Consider `max-h-[60dvh]` or no cap below `lg`.
2. `/admin/commander` Prehľad tab table is five columns wide (Vozidlo, Stav, Pozícia (čas), Adresa, Rýchlosť). Already wraps in `overflow-x-auto`, but horizontal touch-scroll is awkward. Consider a stacked card layout below `md` and/or hiding low-priority columns at narrow widths.
3. Leaflet map height: `h-72 sm:h-96`. On portrait tablet under the cards that reads as cramped. Try `h-[40dvh] sm:h-96`.
4. Card grids on Detail tab use `grid-cols-2 md:grid-cols-4`. At ~768 px effective width the 2-col layout should be fine; verify line-wrap inside individual cards (Pozícia timestamp, Adresa).
5. Leaflet's default zoom +/- buttons are ~26 px square. Enlarge via CSS for touch, or hide them and rely on pinch-to-zoom.
6. Touch targets generally: confirm every `<button>` is at least 44×44 px tall. The "Sledovať live" / "Obnoviť" header buttons should be fine; double-check the ride-list rows and the Detail-tab sidebar items.
7. The kiosk page (`/kiosk`) is the existing tablet gold standard. Use it as the visual reference for spacing, touch-target size and dvh-based heights. Don't degrade what already works there.

None of these are confirmed bugs. They're the surfaces I'd open the device on first.

## Files most likely to change in this session

- `client/src/app/pages/commander/commander.page.html` (the big one)
- `client/src/styles.css` (Leaflet zoom-control sizing, any new component classes)
- `client/src/app/components/commander-car-panel/commander-car-panel.component.html` (embedded on car-detail)
- Possibly minor tweaks to existing pages once you find concrete issues with them

## What you should not need to touch

- Any backend file. The Commander backend is mobile-agnostic; nothing about responsiveness lives there.
- `appsettings*.json`, `SECRETS.md`, `Program.cs` for Commander wiring. Done.
- Any migration file. The Commander work added zero schema. If you do touch the DB for some unrelated reason, see `PROJECT_NOTES.md` → "⚠️ CRITICAL: Migration Safety Rules" first.

## Test plan

1. Local boot: `cd client && ng serve` and `cd API && dotnet run`. Local creds for Commander are already in `API/appsettings.Local.json`.
2. Sign in as `admin` (superadmin). The Commander flag stays ON in the dev DB across restarts; if it isn't, flip it on under Account → Funkcie.
3. Open the Acer's browser on the dev URL (or the Vercel dev preview). Use Add to Home Screen if PWA testing is in scope.
4. In landscape and portrait, walk every admin page:
   - Dashboard, Zamestnanci, Pracoviská, Autá, Materiál, Záznamy, Notifikácie, **Commander (Detail and Prehľad tabs)**, Účet.
5. On `/admin/commander` specifically:
   - Click each vehicle in the sidebar / table. Map should pan to the new selection.
   - Toggle "Sledovať live" on. The timestamp ticks every 30 s. Marker should drift if the vehicle is moving.
   - Click a non-private ride in "Posledné jazdy". Map switches to ride mode (Štart green pill, Cieľ red pill, dashed connector, auto-fit bounds).
   - Click "Späť na živú polohu". Map returns to the live position.
   - Switch tabs Detail ↔ Prehľad. Map re-mounts cleanly.
6. Note anything that looks broken: text clipping, controls below 44×44 px, controls overlapping the navbar, scroll regions inside scroll regions, anything that requires a second tap to land.

## Open backlog (do not derail tablet work)

These are deferred from the Commander integration:

- **Commander M3 — live tracking trail**. The public Commander API does not expose per-second GPS samples, so we cannot replicate Commander's own ride-playback control. The only path is a backend `BackgroundService` that polls `/last-positions` and stores samples in our DB. That is a separate piece of infrastructure. Documented in this session's chat history.
- **`Car.CommanderVehicleId` column.** Plate-match is the current pairing strategy on `/admin/cars/:id`. A real FK would be cleaner; needs an EF migration (see Migration Safety Rules) and a small UI to pick the Commander vehicle for each Car.
- **Per-employee km attribution on `TimeEntry`** (originally listed as M2 in `COMMANDER_PLAN.md`, before we shipped the rides M2 we have now). The plumbing is there: `/api/commander/vehicles/{id}/rides` returns per-ride `startTime`, `stopTime`, `distance`. Mapping rides to clock-in/out windows is server-side work.

If the customer asks about any of these during the tablet session, log it and finish the tablet pass first.

---

*End of handoff.*
