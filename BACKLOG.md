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

- [x] **"Nahrať fotografiu" tab (proof-of-work standalone photos)**
  - Replaced the "Ručný záznam" tab with a new "Nahrať fotografiu" tab on the kiosk
  - Step flow: PIN numpad → location selection → photo capture/pick → result with employee name badge
  - Three camera options: selfie (front camera for identity), rear camera, gallery
  - Photos saved to new `WorkPhotos` table (separate from `TimeEntries`), uploaded to Cloudinary under `work-photos/{locationId}/{year-month}/`
  - Location gallery now unions both TimeEntry photos and standalone WorkPhotos
  - Standalone work photos show a "Foto" badge in the gallery grid; delete works correctly for both types
  - **Note (future):** Consider making front-facing selfie mandatory for stronger identity verification

---

## 📅 Date / Time Rules

- [x] **Maximum 2 days back for hour logging**
  - Kiosk date picker now has `[min]="twoDaysAgoString()"` — workers can only log today, yesterday, or the day before
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
