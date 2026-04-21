# Šichtovnica — Customer Call Backlog

> Translated from SK call notes. Check off items as they are implemented.

---

## ✅ Done

- [x] Rename app title from "Dochádzka / Prehľad" to **Šichtovnica**
- [x] Company logo displayed next to app name on kiosk and admin navbar
- [x] Favicon changed to company logo
- [x] `−`/`+` hour adjustment circles in the hours entry modal made bigger (`w-12` → `w-16`)
- [x] Default date range changed from current week to current month (Reports, Time Entries, My Hours)
- [x] Default admin credentials confirmed: username `vladosroka` / password `Nikolasko1`

---

## 🖼️ Photo / Image Features

- [x] **Photo attachment when logging hours (kiosk clock-in/out flow)**
  - Camera + gallery buttons in the hours modal; HEIC normalised client-side via `heic2any`
  - Photo uploaded to Cloudinary after hours entry is saved; failure is non-blocking

- [x] **Photo attachment on manual entry (manager side)**
  - Admin time-entries add/edit form has photo upload with thumbnail preview and replace/delete

- [x] **Location photo gallery**
  - Grid gallery on the location detail page, filtered by month
  - Individual photo deletion and bulk delete (with date cutoff)
  - "Download all" as ZIP via Cloudinary API

- [x] **Photo storage strategy**
  - Extended existing Cloudinary `IBlobStorageService`; work photos stored under `work-photos/{locationId}/{year-month}/`
  - Admin can download per-location ZIPs and delete old photos

- [x] **HEIC → PNG conversion**
  - Frontend: `heic2any` in `client/src/app/utils/image-utils.ts`
  - Backend: `SixLabors.ImageSharp` in `API/Services/ImageProcessingService.cs` — normalises all uploads to PNG, caps at 2048px
  - **Note:** Run `npm install heic2any` in `client/` once before building

- [x] **"Nahral fotografiu" / "Nenahral fotografiu" status badges on clocked hours**
  - Admin "Záznamy dochádzky" table: Foto column shows thumbnail + green "✓ Nahral" badge, or grey "✗ Nenahral" badge (both desktop table and mobile cards)
  - Kiosk "Moje hodiny" table: new "Fotografia" column with same green/grey badges per row

- [x] **"Moje hodiny" date pickers work on mobile**
  - Removed `appDate` (flatpickr with `disableMobile: true`) from the Od/Do fields in "Moje hodiny"
  - Now uses plain `type="date"` with `[color-scheme:dark]` — triggers native mobile date picker

- [x] **Location gallery delete button visible on mobile**
  - Delete `×` button was `hidden group-hover:flex` (hover-only) — invisible on touch devices
  - Now always visible as `flex` with semi-transparent red background

- [x] **"Nahrať fotografiu" tab (proof-of-work standalone photos)**
  - Replaced the "Ručný záznam" tab with a new "Nahrať fotografiu" tab on the kiosk
  - Step flow: PIN numpad → location selection → photo capture/pick → result with employee name badge
  - Three camera options: selfie (front camera for identity), rear camera, gallery
  - Photos saved to new `WorkPhotos` table (separate from `TimeEntries`), uploaded to Cloudinary under `work-photos/{locationId}/{year-month}/`
  - Location gallery now unions both TimeEntry photos and standalone WorkPhotos
  - Standalone work photos show a "Foto" badge in the gallery grid; delete works correctly for both types
  - **Note (future):** Consider making front-facing selfie mandatory for stronger identity verification

---

## 📱 Mobile / PWA UX (April 15 call)

- [x] **Disable double-tap zoom on mobile/tablet**
  - Viewport meta updated: `maximum-scale=1, user-scalable=no`
  - Global CSS: `touch-action: manipulation` on `<html>` — disables double-tap zoom while keeping normal scroll/tap

- [x] **Fix PWA app icon resolution (distorted on home screen)**
  - Original logo was 1920×1280 (not square) — looked stretched/cropped when added to home screen
  - Generated proper square icons: 512×512, 192×192, and 180×180 (apple-touch-icon)
  - Updated `manifest.webmanifest` with separate `any` and `maskable` purpose entries at correct sizes
  - Updated `index.html` to reference the new square icons

- [x] **Add car (Auto) column to "Moje Hodiny" kiosk view**
  - Workers can now see which car was used for each entry when checking their logged hours
  - Shows 🚗 + car name, or "—" if no car

- [x] **"Záznamy" default to full month with week toggle**
  - Default date range now covers the entire current month (1st to last day), not just up to today
  - Added "Mesiac / Týždeň" toggle buttons above the date filters
  - Manual date changes switch to "custom" mode so the toggle doesn't override user picks

- [x] **Remove Príchod / Odchod columns from "Moje hodiny", add Dátum column**
  - Table on the kiosk "Moje hodiny" view no longer shows the exact clock-in / clock-out timestamps
  - Added a Dátum column (formatted `dd.MM.yyyy` from `clockIn`) as the first column so workers can still tell which day each row belongs to
  - Final column order: Dátum, Pracovisko, Auto, Hodiny, Foto záznamu, Poznámka
  - Footer "Spolu" colspan adjusted accordingly (3 → Dátum + Pracovisko + Auto)

- [x] **Spolu column on kiosk weekly overview = full-month total**
  - The per-employee "Spolu" column next to the 7-day grid now sums hours for the whole calendar month that contains the currently viewed week, not just the 7 visible days
  - Backend `GET /api/kiosk/overview` expands its query range to cover the whole month and restricts `TotalHours` to that month; the daily cells still only show the 7 days of the viewed week

- [x] **Manual time entry (admin) is now hours-based, not clockIn/clockOut**
  - Replaced Príchod / Odchod date+time fields on the admin Záznamy dochádzky add and edit forms with a single Dátum picker and a Počet hodín control (±0.5h buttons plus preset chips: 0.5, 1, 2, 4, 5, 5.5, 6, 7, 7.5, 8, 9, 10)
  - Mirrors the kiosk "log hours" UX — easier/faster for managers
  - Frontend back-calculates clockIn/clockOut before POST/PUT using the same convention as `/api/kiosk/log-hours`: past date → clockOut = 17:00 that day; today → clockOut = now; clockIn = clockOut − hoursWorked. No backend/API change.

- [x] **"Týždeň" quick-range button actually updates the Od/Do pickers**
  - The "Mesiac / Týždeň" toggle on `Záznamy dochádzky` was reassigning `from`/`to` component fields, but flatpickr's visible alt-input didn't re-render because the directive only read `input.value` at initialisation
  - `DatepickerDirective` now implements `ControlValueAccessor`, so any programmatic `[(ngModel)]` write (e.g. "Týždeň") calls `fp.setDate(...)` and the picker label updates in-place
  - Side benefit: the same fix applies to any future programmatic date changes on the Reports/Time-entries pages

- [x] **Admin add-entry dropdown filters out inactive employees + backend guard**
  - A manually added entry for a deactivated employee would be invisible in the kiosk "Moje hodiny" view, because `FindEmployeeByPin` only resolves `IsActive = true` employees. This was the root cause of the "worker can't see their entry" bug reported on 2026-04-21.
  - The add form now uses `activeEmployees()` (computed from `employees().filter(e => e.isActive)`); the filter dropdown above the list still shows everyone so historical entries remain searchable.
  - `POST /api/time-entries` also rejects creates for inactive employees with a clear Slovak error message, so the frontend filter is a defensive layer rather than the sole gate.

- [x] **Moje hodiny surfaces backend errors instead of silent empty state**
  - When the kiosk returned 401 (wrong PIN) or another error, the UI previously cleared entries and showed "Žiadne záznamy za toto obdobie" — indistinguishable from a legitimate empty month
  - Added a `myHoursError` signal; the result card now renders the backend message in red when the request fails

---

## 📅 Date / Time Rules

- [x] **Maximum 2 days back for hour logging**
  - Kiosk date picker has `[min]="twoDaysAgoString()"` and `[max]="todayString()"`
  - Added `clampSelectedDate()` called on `(change)` and before every `submitHours()` — enforces the range in TypeScript so mobile browsers that allow typing/scrolling past min/max cannot bypass it
  - Manager manual entry remains unrestricted

- [ ] **Date column in CSV export**
  - The CSV export currently has: Employee, Location, Car, Hours, Note
  - Add a **Date** column to the export

---

## 🔔 Notifications & Reminders

- [ ] **48-hour reminder system**
  - If an employee has not logged any hours in the last 48 hours, send a reminder
  - Needs to be scheduled (cron-style background job or scheduled task)

- [ ] **Internal SMS reminder tester**
  - Add a dev/admin tool to manually trigger a test SMS reminder to a given number
  - For verifying SMS delivery without waiting for the scheduled trigger

- [ ] **SMS reminders via universal address**
  - Research and implement SMS delivery via a universal/gateway address (e.g. email-to-SMS, Twilio, or Slovak carrier gateway)
  - Goal: send reminder SMS without a dedicated SMS provider if possible

- [ ] **General notifications research**
  - Evaluate push notification options for the PWA (web push / service worker)
  - Decide between push notifications vs SMS vs both

---

## 📱 PWA / Mobile

- [ ] **Add to home screen guide — iOS**
  - Create a short in-app or PDF guide for adding the web app to the iOS home screen (Safari → Share → Add to Home Screen)

- [ ] **Add to home screen guide — Android**
  - Create a short in-app or PDF guide for Android (Chrome → three-dot menu → Add to Home Screen / Install App)

- [ ] **App icon for PWA (from email)**
  - Customer sent an app icon image via email — add it as the PWA manifest icon (`manifest.json` `icons` array) so it appears correctly when installed on home screen

---

## ⚙️ Technical / Infrastructure

- [ ] **Developer vs Production build configuration**
  - Set up separate environment configs (`environment.ts` / `environment.prod.ts`) with distinct API URLs, feature flags, logging levels
  - Ensure `ng build --configuration production` targets production API and disables dev tooling

---

## ❓ To Clarify / Investigate

- [x] **Red tile for employees with no hours today**
  - Employee tiles on the kiosk main view turn red (border + background tint) when that employee has no completed entries for today
  - Only flags today — past days will be handled separately via SMS/email reminders
  - Flag disappears automatically if today is not in the current week view
