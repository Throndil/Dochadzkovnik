# Cloudinary — asset storage & multi-customer folders

All uploaded files (work photos, invoice PDFs, receipts, diary attachments,
employee/car/machine/site photos) go to Cloudinary via
`API/Services/BlobStorageService.cs`.

## Folder layout

Every asset is stored under a **per-customer root folder**, then by category,
then by period/project:

```
{root}/                         ← one per customer (Cloudinary:ProjectFolder)
  work-photos/{projectId}-{slug}/{YYYY-MM}/{guid}
  material-photos/{projectId}/{YYYY-MM}/{guid}
  invoices/{YYYY-MM}/{invoiceNo}-{guid}.pdf
  receipts/{YYYY-MM}/{guid}
  work-diaries/{YYYY-MM}/{guid}
  employee-photos/{guid}        ← one image per record (flat is fine)
  car-photos/{guid}
  machine-photos/{guid}
  location-photos/{guid}
```

The path lives inside each asset's `public_id`, so nothing is ever dumped at
the account root. Folder segments come from `CloudinaryFolders.cs`.

## Adding a new customer

We keep the **same** Railway/Vercel and **one shared Cloudinary account**;
customers are separated by their root folder. Each new customer gets:

1. **Its own database** (separate Postgres / connection string).
2. **Its own Cloudinary root folder** — set `Cloudinary__ProjectFolder=<slug>`
   (e.g. `acme`) on that deployment. If unset it falls back to `profistav`
   (the first customer) **and logs a warning at startup** — watch for
   `Cloudinary:ProjectFolder is not set` in the logs when onboarding.
3. The same `Cloudinary__CloudName / __ApiKey / __ApiSecret` (shared account).

The first/original install (`profistav`) does not set the variable and uses the
default. To silence its startup warning and make it explicit, set
`Cloudinary__ProjectFolder=profistav` on that deployment too.

> When we get a lot of customers and the shared account gets tight, revisit:
> either a paid Cloudinary plan, or a separate Cloudinary account per customer
> (each with its own free 25-credit tier).

## Usage snapshot (2026-07-21)

Account `dgmhnqr29`, **Free plan** — **1.06 / 25 credits (4.2 %)**:

| Metric | Value | Credits |
|---|---|---|
| Storage | ~558 MB (~500 files) | 0.54 |
| Bandwidth (this month) | ~367 MB | 0.36 |
| Transformations | 162 | 0.16 |

Free tier = 25 credits/month, where 1 credit ≈ 1 GB storage **or** 1 GB
bandwidth **or** 1,000 transformations.

**When to upgrade:** watch the credits % on the Cloudinary dashboard. Consider
a paid plan (or per-customer accounts) when **cumulative storage approaches
~15–20 GB**, **monthly bandwidth approaches ~20 GB**, or credits sit above
~70 %. At the current ~1 credit/month per customer, the free tier comfortably
covers several customers before that matters.

## Notes

- The account is in Cloudinary's legacy **fixed-folder** mode, so the Media
  Library tree only shows `samples` even though asset paths are correct.
  Enabling **dynamic folders** would materialise the tree in the dashboard —
  purely cosmetic, doesn't change uploads.
