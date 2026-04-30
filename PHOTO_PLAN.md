<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Photo Upload — Implementation Plan

> Based on customer call notes. Covers: work photo capture, location galleries,
> HEIC→PNG pipeline, storage, and admin download/delete.

---

## Overview

There are two distinct photo concepts in this feature:

| Concept | What it is | Who uploads |
|---|---|---|
| **Work photo** | Photo of what was done on a job site, attached to a TimeEntry | Worker (kiosk) or Manager (admin) |
| **Location gallery** | All work photos ever submitted for a construction site | Derived automatically — no separate upload |

Work photos feed into location galleries automatically — there is no separate gallery upload.

---

## Phase 1 — Image Processing Pipeline (backend foundation)

Everything else depends on this. All uploaded images — employee photos, location photos, work photos — must be normalised to PNG before hitting Cloudinary.

### 1.1 Frontend HEIC → PNG conversion (`heic2any`)
iPhone cameras produce `.heic` / `.heif` files by default. Browsers cannot display or upload these natively. Convert them client-side **before** the file reaches the server.

- Install: `npm install heic2any` in `client/`
- Create a shared Angular utility `image-utils.ts`:
  ```
  normaliseFile(file: File): Promise<File>
  ```
  - If `file.type === 'image/heic'` or extension is `.heic/.heif` → use `heic2any` to convert to PNG blob, return as new `File`
  - Otherwise return the file unchanged
- This utility is called in every upload component before the HTTP request

### 1.2 Backend image normalisation (`SixLabors.ImageSharp`)
Even after HEIC conversion, images may be JPEG, WebP, BMP etc. Normalise server-side to PNG and cap resolution at 2048px on the longest edge to keep Cloudinary storage reasonable.

- Install NuGet: `SixLabors.ImageSharp`
- Create `API/Services/ImageProcessingService.cs`:
  ```
  Task<Stream> NormaliseToPngAsync(Stream input, int maxDimension = 2048)
  ```
  - Detects format from stream header (no reliance on file extension)
  - Resizes if wider/taller than `maxDimension` (maintains aspect ratio)
  - Encodes as PNG
  - Returns resulting `Stream`
- Wire into `BlobStorageService.UploadAsync` as a preprocessing step so **all** uploads go through it automatically — employee photos, location photos, and work photos all benefit

---

## Phase 2 — TimeEntry Work Photo (backend)

### 2.1 Model + Migration
Add `PhotoUrl` to `TimeEntry`:

```csharp
public string? PhotoUrl { get; set; }
```

Create EF migration: `AddTimeEntryPhotoUrl`

### 2.2 DTOs
- `TimeEntryDto` — add `PhotoUrl`
- `CreateTimeEntryDto` — do NOT add photo here (multipart files and JSON don't mix cleanly)
- `KioskLogHoursDto` — same: no photo in the JSON body

### 2.3 Photo upload endpoint
Add a dedicated endpoint so the photo upload is separate from the data write:

```
POST /api/time-entries/{id}/photo
Content-Type: multipart/form-data
→ Returns: { photoUrl: "https://..." }

DELETE /api/time-entries/{id}/photo
→ Removes from Cloudinary + clears PhotoUrl
```

The kiosk and admin flows:
1. Submit hours → get back `timeEntryId`
2. If photo selected → POST photo to `/{id}/photo`

This keeps all endpoints clean and avoids multipart/JSON mixing.

### 2.4 Cloudinary folder structure
Work photos stored as: `work-photos/{locationId}/{year-month}/{guid}`

This makes per-location, per-month querying trivial from the DB (filter TimeEntries), and keeps Cloudinary organised if you ever need to browse it manually.

---

## Phase 3 — Kiosk: Photo Capture in Hours Modal

### 3.1 UI change — hours step
After the existing hours/note fields, add an optional photo section:

```
[ 📷 Take photo ]  [ 🖼️ From gallery ]

[thumbnail preview if selected]  [× remove]
```

- `📷 Take photo` → `<input type="file" accept="image/*" capture="environment">` (opens rear camera directly on mobile)
- `🖼️ From gallery` → `<input type="file" accept="image/*">` (opens gallery picker)
- Both hidden inputs triggered by the buttons
- On file selected: run `normaliseFile()` → show thumbnail preview
- Photo is held in component state until "Potvrdiť" is pressed

### 3.2 Submit flow change
Current: `submitHours()` → calls `log-hours` → done

New:
1. `submitHours()` → calls `log-hours` → receives `timeEntryId`
2. If photo staged → `POST /api/time-entries/{id}/photo`
3. Show result step (success/error)

If photo upload fails, the hours are still saved — show a warning rather than blocking.

---

## Phase 4 — Admin: Photo in Time Entries & Manual Entry

### 4.1 Time entries list
- Add a small thumbnail column to the time entries table
- Clicking the thumbnail opens it full-screen (lightbox or new tab)

### 4.2 Add/edit form
- Add the same photo capture UI (camera + gallery buttons + preview)
- On create: upload photo after entry is created
- On edit: show existing photo if any; allow replacing or deleting

### 4.3 Manual entry on kiosk
- Same as admin form — add optional photo section below the note field

---

## Phase 5 — Location Photo Gallery

No new database model needed. The gallery is derived from TimeEntries.

### 5.1 Backend endpoint
```
GET /api/locations/{id}/photos?from=YYYY-MM&to=YYYY-MM
→ Returns: list of { timeEntryId, employeeName, date, photoUrl }
```

This is a simple query:
```sql
SELECT t.Id, t.PhotoUrl, t.ClockIn, e.FirstName, e.LastName
FROM TimeEntries t
JOIN Employees e ON t.EmployeeId = e.Id
WHERE t.LocationId = @id
  AND t.PhotoUrl IS NOT NULL
  AND t.ClockIn >= @from AND t.ClockIn < @to
ORDER BY t.ClockIn DESC
```

### 5.2 Location detail page (admin)
Add a "Fotky" (Photos) tab to the location detail page:
- Month/year picker to browse by period
- Masonry or grid layout of thumbnails
- Each thumbnail shows employee name + date on hover
- "Download all" button for the selected month
- Admin can delete individual photos (calls `DELETE /api/time-entries/{id}/photo`)

---

## Phase 6 — Photo Download & Deletion (Admin)

### 6.1 Individual download
Cloudinary URLs are publicly accessible — "Download" opens the URL in a new tab or triggers browser download via an anchor with `download` attribute.

### 6.2 Bulk download (ZIP)
Two options (pick one):
- **Option A — Server-side ZIP**: API fetches photos from Cloudinary, streams a ZIP back to the browser. Simple but uses server memory.
- **Option B — Cloudinary ZIP API**: Cloudinary supports `https://api.cloudinary.com/v1_1/{cloud}/resources/download` which generates a downloadable ZIP from a list of public IDs. No server memory used. Recommended.

Endpoint:
```
GET /api/locations/{id}/photos/download?from=YYYY-MM&to=YYYY-MM
→ Redirects to Cloudinary ZIP URL or streams ZIP
```

### 6.3 Bulk deletion
Admin can delete all photos for a location older than a chosen date. This:
1. Fetches affected TimeEntries
2. Calls `Cloudinary.DestroyAsync` for each PhotoUrl (already implemented in `BlobStorageService.DeleteAsync`)
3. Sets `PhotoUrl = null` on all affected TimeEntries
4. Returns count of deleted photos

Endpoint:
```
DELETE /api/locations/{id}/photos?before=YYYY-MM-DD
```

---

## Implementation Order

```
Phase 1  →  Phase 2  →  Phase 3  →  Phase 4  →  Phase 5  →  Phase 6
Pipeline     Backend      Kiosk UI    Admin UI    Gallery     Manage
(2–3h)       (2h)         (3h)        (2h)        (2h)        (2h)
```

Total estimated: ~13–15 hours of implementation work.

Start with Phase 1 — once the image pipeline is solid, all subsequent phases
plug into it cleanly and consistently.

---

## Open Questions

1. **Photo required or optional?** — Assume optional everywhere for now. Worker can skip.
2. **Max file size?** — Suggest 10MB pre-conversion (ImageSharp will compress to PNG efficiently).
3. **Retention policy** — How long should work photos be kept? Customer to decide; deletion tool in Phase 6 handles it.
4. **Gallery visibility** — Should workers be able to see the location gallery from the kiosk? Not in scope for now; kiosk only uploads, admin views.
