using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace API.DTOs;

// Auth
public class LoginDto
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
    /// <summary>Second step of the login when the account has a security PIN.</summary>
    public string? Pin { get; set; }
}

public class AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>True = password OK but the account requires a PIN — no token
    /// yet; the client shows the PIN step and repeats login with Pin set.</summary>
    public bool PinRequired { get; set; }
}

public class UpdateDisplayNameDto
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;
}

public class UpdateAdminPinDto
{
    /// <summary>Required when a PIN is already set (change needs the old one).</summary>
    public string? CurrentPin { get; set; }
    [Required, RegularExpression(@"^\d{4,8}$", ErrorMessage = "PIN musí mať 4 až 8 číslic.")]
    public string NewPin { get; set; } = string.Empty;
}

public class ForgotPasswordDto
{
    [Required] public string Username { get; set; } = string.Empty;
}

public class ResetPasswordDto
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Token { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 6)] public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordDto
{
    [Required] public string CurrentPassword { get; set; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 6)] public string NewPassword { get; set; } = string.Empty;
}

public class UpdateRecoveryEmailDto
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
}

// Employee
public class CreateEmployeeDto
{
    [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
    [Required, StringLength(6, MinimumLength = 4)] public string Pin { get; set; } = string.Empty;
    [StringLength(30)] public string? PhoneNumber { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(100)] public string? City { get; set; }
}

public class UpdateEmployeeDto
{
    [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
    [StringLength(30)] public string? PhoneNumber { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(100)] public string? City { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>EUR/h. Optional; NULL leaves the existing value untouched on PUT.</summary>
    [Range(0, 1_000_000)] public decimal? HourlyWage { get; set; }
    /// <summary>"profistav" | "stroje" (Fáza D8). NULL leaves untouched.</summary>
    public string? Division { get; set; }
    /// <summary>Free-text pozícia (F6): "šofér", "murár"… NULL leaves untouched;
    /// empty string clears.</summary>
    [StringLength(100)] public string? Position { get; set; }
}

public class SetPinDto
{
    [Required, StringLength(6, MinimumLength = 4)] public string Pin { get; set; } = string.Empty;
}

public class EmployeeDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? PinPlain { get; set; }
    /// <summary>Reason given when the worker declined push notifications from the kiosk banner. Null if never declined.</summary>
    public string? NotificationsDeclineReason { get; set; }
    /// <summary>
    /// EUR/h. NULL when no rate has been set; admin Mzdy view shows
    /// "Sadzba nenastavená" in amber and any new TimeEntry inserted while
    /// NULL snapshots WageAtTime = 0. Admin-only field; the kiosk view
    /// uses a separate DTO that doesn't include it.
    /// </summary>
    public decimal? HourlyWage { get; set; }
    /// <summary>"profistav" | "stroje" — company division (Fáza D8).</summary>
    public string Division { get; set; } = "profistav";
    /// <summary>Free-text pozícia (F6), e.g. "šofér".</summary>
    public string? Position { get; set; }
}

// Fuel cards (F6)
public class FuelCardDto
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int? EmployeeId { get; set; }
    /// <summary>Holder's display name, resolved server-side. Null when unassigned.</summary>
    public string? EmployeeName { get; set; }
    /// <summary>Holder's pozícia — shown on the cards page. Null when unassigned.</summary>
    public string? EmployeePosition { get; set; }
    public bool IsActive { get; set; }
}

public class SaveFuelCardDto
{
    [Required, StringLength(100, MinimumLength = 1)] public string Label { get; set; } = string.Empty;
    [StringLength(500)] public string? Note { get; set; }
    /// <summary>Holder. Null/0 = unassigned (holder may not be in the system yet).</summary>
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
}

// Location
public class CreateLocationDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Address { get; set; }
}

public class UpdateLocationDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LocationDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; }
}

// Time Entry
// MachineId ("Mašina/Tatra") mirrors CarId on all three time-entry DTOs.
public class CreateTimeEntryDto
{
    [Required] public int EmployeeId { get; set; }
    [Required] public int LocationId { get; set; }
    public int? CarId { get; set; }
    public int? MachineId { get; set; }
    [Required] public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public string? Note { get; set; }
}

public class UpdateTimeEntryDto
{
    public int? CarId { get; set; }
    public int? MachineId { get; set; }
    [Required] public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public string? Note { get; set; }
}

public class TimeEntryDto
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? EmployeePhotoUrl { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int? CarId { get; set; }
    public string? CarName { get; set; }
    public int? MachineId { get; set; }
    public string? MachineName { get; set; }
    public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public double? HoursWorked { get; set; }
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }

    /// <summary>
    /// True when the worker picked "Pokračovať bez dôkazu" on the kiosk
    /// proof-of-work step. Used by the admin Foto column to distinguish
    /// "skipped on purpose" from "forgot". See PROOF_OF_WORK_UX_PLAN.md §(d).
    /// </summary>
    public bool ProofOfWorkSkipped { get; set; }

    /// <summary>
    /// True when at least one <see cref="Models.WorkDiary"/> is linked to this
    /// time entry. Server-computed so the admin Foto column can render
    /// "✓ Denník" without a second round-trip.
    /// </summary>
    public bool HasDiary { get; set; }

    /// <summary>
    /// Body of the linked <see cref="Models.WorkDiary"/>, if any. Surfaced on
    /// the admin Záznamy dochádzky table as its own column so managers can read
    /// the diary content inline without opening the Location detail page.
    /// </summary>
    public string? DiaryBody { get; set; }
}

// Kiosk
public class ClockInDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required] public int LocationId { get; set; }
}

public class ClockOutDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class DeclineNotificationsDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public class ManualEntryDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required] public int LocationId { get; set; }
    [Required] public DateTime ClockIn { get; set; }
    [Required] public DateTime ClockOut { get; set; }
    public string? Note { get; set; }
}

public class LogHoursDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required] public int LocationId { get; set; }
    [Required, Range(0.25, 24)] public double HoursWorked { get; set; }
    public int? CarId { get; set; }
    /// <summary>Kiosk transport choice Stroj (Fáza F3) — mutually exclusive
    /// with CarId; both null = pešo.</summary>
    public int? MachineId { get; set; }
    public string? Note { get; set; }
    public DateTime? Date { get; set; }

    /// <summary>
    /// Set true when the kiosk's new proof-of-work step records an explicit
    /// "Pokračovať bez dôkazu" choice. Optional / nullable so the existing
    /// flag-off kiosk (no proof-of-work step) keeps working unchanged.
    /// See PROOF_OF_WORK_UX_PLAN.md §(d).
    /// </summary>
    public bool? ProofOfWorkSkipped { get; set; }
}

// ─── WorkDiary (stavebný denník) — see PROOF_OF_WORK_UX_PLAN.md ───
public class WorkDiaryDto
{
    public int Id { get; set; }
    public int? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public int LocationId { get; set; }
    public string? LocationName { get; set; }
    public int? TimeEntryId { get; set; }
    public DateTime Date { get; set; }
    public string BodyText { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateKioskWorkDiaryDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required] public int LocationId { get; set; }
    [Required] public DateTime Date { get; set; }
    [Required, StringLength(20000, MinimumLength = 1)] public string BodyText { get; set; } = string.Empty;
    public int? TimeEntryId { get; set; }
}

public class UpdateWorkDiaryDto
{
    public DateTime? Date { get; set; }
    [StringLength(20000)] public string? BodyText { get; set; }
}

/// <summary>
/// Body for the kiosk auto-skip lookup. PIN-validated.
/// See PROOF_OF_WORK_UX_PLAN.md §"Auto-skip".
/// </summary>
public class ProofExistsRequestDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required] public int LocationId { get; set; }
    public DateTime? Date { get; set; }
}

public class ProofExistsDto
{
    public bool Exists { get; set; }
    /// <summary>"photo" | "diary" | null. Helps the kiosk pick the right Slovak hint copy.</summary>
    public string? Source { get; set; }
    /// <summary>Europe/Bratislava local timestamp of the most recent matching proof, or null.</summary>
    public DateTime? At { get; set; }
}

/// <summary>
/// Body for the kiosk roll-up that shows today's entries at the picked Location.
/// PIN-validated. Read-only — workers can only see who else clocked at this
/// site today and read their notes.
/// </summary>
public class TodayAtLocationRequestDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    [Required] public int LocationId { get; set; }
}

// ─── Invoice scanning (see INVOICE_SCANNING_PLAN.md) ───────────────

public class InvoiceDocumentDto
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string? SupplierIco { get; set; }
    public string? SupplierIcDph { get; set; }
    public string? SupplierIban { get; set; }
    public DateTime IssueDate { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal TotalExclVat { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalInclVat { get; set; }
    public string PdfUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    /// <summary>"invoice" | "receipt" (pokladničný blok).</summary>
    public string DocumentKind { get; set; } = "invoice";
    /// <summary>"profistav" (stavby) | "stroje" — company division.</summary>
    public string Division { get; set; } = "profistav";
    /// <summary>"cost" (výdaj) | "income" (príjem — AZ issued the invoice).</summary>
    public string Direction { get; set; } = "cost";
    /// <summary>Informational mašina/auto backtrack (F1) — never affects sums.</summary>
    public int? MachineId { get; set; }
    public string? MachineName { get; set; }
    public int? CarId { get; set; }
    public string? CarName { get; set; }
    public bool ReconciliationOk { get; set; }
    public string? ReconciliationNote { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string? CommittedBy { get; set; }
    public DateTime? CommittedAt { get; set; }
    public string? Note { get; set; }
    /// <summary>
    /// "file" when uploaded via PDF/image picker; "camera" when assembled
    /// from photos taken in the in-app scanner. Drives the list-page icon.
    /// </summary>
    public string ScanSource { get; set; } = "file";

    /// <summary>Distinct effective Pracovisko names across this document's
    /// lines (line override ?? delivery list) — chips on the Faktúry list.</summary>
    public List<string> LocationNames { get; set; } = new();
    /// <summary>Number of photos in the camera scan. Null on file uploads.</summary>
    public int? ScanPageCount { get; set; }
    public List<InvoiceDeliveryListDto> DeliveryLists { get; set; } = [];
}

public class InvoiceDeliveryListDto
{
    public int Id { get; set; }
    public string? DeliveryNoteRef { get; set; }
    public DateTime PurchaseDate { get; set; }
    public string? PickedUpBy { get; set; }
    public string? DeliveryNote { get; set; }
    public int? LocationId { get; set; }
    public string? LocationName { get; set; }
    /// <summary>Mašina/Auto assignment (stroje docs) — F1.</summary>
    public int? MachineId { get; set; }
    public int? CarId { get; set; }
    /// <summary>Auto-suggested site name from "akcia:". Null when blank or already mapped.</summary>
    public string? AkciaSuggestion { get; set; }
    public decimal? SubtotalExclVat { get; set; }
    public decimal? SubtotalVat { get; set; }
    public List<InvoiceLineDto> Lines { get; set; } = [];
}

public class InvoiceLineDto
{
    public int Id { get; set; }
    public int PurchaseId { get; set; }
    public string? SupplierItemCode { get; set; }
    public string MaterialNameRaw { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal? ListPriceExclVat { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? UnitPriceInclVat { get; set; }
    public decimal VatRate { get; set; }
    public bool IsReverseCharge { get; set; }
    public bool IsService { get; set; }
    /// <summary>Per-line site override. Null = inherits the delivery list's Location.</summary>
    public int? LocationId { get; set; }
    /// <summary>Name of the line's own Location when overridden; null when inheriting.</summary>
    public string? LocationName { get; set; }
    /// <summary>Per-line Mašina/Auto override (stroje docs) — F1.</summary>
    public int? MachineId { get; set; }
    public int? CarId { get; set; }
}

public class UpdateInvoiceLineDto
{
    [StringLength(50)] public string? SupplierItemCode { get; set; }
    [StringLength(200)] public string? MaterialNameRaw { get; set; }
    [StringLength(50)]  public string? Unit { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    public decimal? VatRate { get; set; }
    /// <summary>Informational zľava %. 0 or negative clears it. Null = unchanged.</summary>
    public decimal? DiscountPercent { get; set; }
    public bool? IsReverseCharge { get; set; }
    public bool? IsService { get; set; }
    /// <summary>
    /// Per-line site override. A positive Location.Id assigns this row to that
    /// site; -1 / 0 clears the override so the row follows its delivery list
    /// again. Null (omitted) leaves the current value unchanged.
    /// </summary>
    public int? LocationId { get; set; }
    /// <summary>Per-line Mašina/Auto override — same sentinel semantics.</summary>
    public int? MachineId { get; set; }
    public int? CarId { get; set; }
}

public class UpdateInvoiceDeliveryListDto
{
    public int? LocationId { get; set; }
    /// <summary>Mašina/Auto assignment (stroje docs): positive id assigns
    /// (clears the other), -1/0 clears. Null = unchanged.</summary>
    public int? MachineId { get; set; }
    public int? CarId { get; set; }
    [StringLength(200)] public string? PickedUpBy { get; set; }
    [StringLength(2000)] public string? DeliveryNote { get; set; }
}

public class TodayAtLocationEntryDto
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public DateTime ClockIn { get; set; }
    public double? HoursWorked { get; set; }
    public string? Note { get; set; }
    /// <summary>
    /// Body of the linked WorkDiary, if the worker submitted via the diary tile.
    /// Null when there is no linked diary. Surfaced on the kiosk roll-up so the
    /// next worker can read what colleagues wrote in their diary entries.
    /// </summary>
    public string? DiaryBody { get; set; }
    /// <summary>True when this row is the requesting worker's own entry. Lets the kiosk highlight it.</summary>
    public bool IsMine { get; set; }
}

// Car
public class CarDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LicensePlate { get; set; }
    public string? PhotoUrl { get; set; }
    /// <summary>"profistav" | "stroje" — vehicle's division (Fáza F).</summary>
    public string Division { get; set; } = "profistav";
    public bool IsActive { get; set; }
    /// <summary>Informational: s-DPH sum of cost documents tagged to this
    /// vehicle (F1 backtrack).</summary>
    public decimal CostTotal { get; set; }
}

public class CreateCarDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(20)] public string? LicensePlate { get; set; }
    /// <summary>"profistav" | "stroje"; anything else falls back to profistav.</summary>
    public string? Division { get; set; }
}

public class UpdateCarDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(20)] public string? LicensePlate { get; set; }
    /// <summary>"profistav" | "stroje". Null leaves untouched.</summary>
    public string? Division { get; set; }
    public bool IsActive { get; set; } = true;
}

// Machines (AZ Stroje, Fáza F0)
public class MachineDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Informational: s-DPH sum of cost documents tagged to this
    /// mašina (F1 backtrack). Division money never computes here.</summary>
    public decimal CostTotal { get; set; }
}

public class CreateMachineDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Note { get; set; }
}

public class UpdateMachineDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One cost document effectively tagged to a car/mašina (F4 —
/// per-asset service ledger). Amount is s DPH, informational only (D4).</summary>
public class AssetCostDocDto
{
    public int InvoiceDocumentId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    /// <summary>"invoice" | "receipt".</summary>
    public string DocumentKind { get; set; } = "invoice";
    /// <summary>"review" | "committed".</summary>
    public string Status { get; set; } = string.Empty;
    public decimal GrossTotal { get; set; }
}

public class MyHoursDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public class KioskStatusDto
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public bool IsClockedIn { get; set; }
    public DateTime? ClockInTime { get; set; }
    public string? LocationName { get; set; }
}

public class KioskResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int? TimeEntryId { get; set; }
}

public class PhotoUploadResultDto
{
    public string PhotoUrl { get; set; } = string.Empty;
}

public class LocationPhotoDto
{
    public int? TimeEntryId { get; set; }   // null for standalone WorkPhotos
    public int? WorkPhotoId { get; set; }   // null for TimeEntry photos
    public string EmployeeName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string PhotoUrl { get; set; } = string.Empty;
}

public class WorkPhotoResultDto
{
    public string PhotoUrl { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int RemainingToday { get; set; }  // how many more uploads the worker can do today
}

// Kiosk weekly overview (public)
public class WeeklyOverviewDto
{
    public DateTime WeekStart { get; set; }
    public List<DateTime> Days { get; set; } = [];
    public List<WeeklyRowDto> Rows { get; set; } = [];
    /// <summary>True when the 7-day window crosses a month boundary (e.g. week of 27 Apr – 3 May).</summary>
    public bool SpansTwoMonths { get; set; }
    /// <summary>Slovak abbreviated name of the first month (e.g. "apr"). Always set.</summary>
    public string? Month1Label { get; set; }
    /// <summary>Slovak abbreviated name of the second month (e.g. "máj"). Set only when SpansTwoMonths is true.</summary>
    public string? Month2Label { get; set; }
}

public class WeeklyRowDto
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public List<WeeklyDayDto> Days { get; set; } = [];
    /// <summary>Full-month total for the first (or only) calendar month of the viewed week.</summary>
    public double TotalHours { get; set; }
    /// <summary>Full-month total for the second calendar month when SpansTwoMonths is true; 0 otherwise.</summary>
    public double TotalHoursMonth2 { get; set; }
}

public class WeeklyEntryDto
{
    public string LocationName { get; set; } = string.Empty;
    public double Hours { get; set; }
    public string? Note { get; set; }
}

public class WeeklyDayDto
{
    public DateTime Date { get; set; }
    public List<WeeklyEntryDto> Entries { get; set; } = [];
}

// Materials catalogue
public class MaterialDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    /// <summary>Current catalogue price per unit, in EUR.</summary>
    public decimal PricePerUnit { get; set; }
    public bool IsActive { get; set; }
}

public class CreateMaterialDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(50)]  public string Unit { get; set; } = string.Empty;
    [Range(0, 1_000_000)] public decimal PricePerUnit { get; set; } = 0m;
}

public class UpdateMaterialDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(50)]  public string Unit { get; set; } = string.Empty;
    [Range(0, 1_000_000)] public decimal PricePerUnit { get; set; } = 0m;
    public bool IsActive { get; set; } = true;
}

// Material usage (per location)
// ─── Denník podľa dátumu (P1) — the per-day "zložka" of a workplace ───

/// <summary>One calendar day of a workplace's folder: shifts, diary entries,
/// material, documents and photos that happened that day.</summary>
public class DailyLogDayDto
{
    public DateTime Date { get; set; }
    public List<DailyLogShiftDto> Shifts { get; set; } = new();
    public List<DailyLogDiaryDto> Diaries { get; set; } = new();
    public List<MaterialUsageDto> Materials { get; set; } = new();
    public List<DailyLogDocDto> Documents { get; set; } = new();
    public List<DailyLogPhotoDto> Photos { get; set; } = new();
}

public class DailyLogShiftDto
{
    public string EmployeeName { get; set; } = string.Empty;
    /// <summary>Null while the shift is still open (no clock-out yet).</summary>
    public decimal? Hours { get; set; }
    public string? Note { get; set; }
    public string? CarName { get; set; }
    public string? MachineName { get; set; }
}

public class DailyLogDiaryDto
{
    public string EmployeeName { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }
}

/// <summary>A delivery list / receipt / purchase targeted at the workplace.</summary>
public class DailyLogDocDto
{
    public int PurchaseId { get; set; }
    public int? InvoiceDocumentId { get; set; }
    public string? SupplierName { get; set; }
    public string? DeliveryNoteRef { get; set; }
    public decimal TotalCost { get; set; }
}

public class DailyLogPhotoDto
{
    public string PhotoUrl { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
}

public class MaterialUsageDto
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    /// <summary>Snapshot of catalogue price at the time the usage was recorded (EUR/Unit).</summary>
    public decimal UnitPriceAtTime { get; set; }
    /// <summary>Convenience field = Quantity * UnitPriceAtTime (EUR).</summary>
    public decimal LineCost { get; set; }
    public DateTime Date { get; set; }
    public int? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }

    /// <summary>
    /// True when this row is a read-side synthesis from a <c>MaterialPurchaseLine</c>
    /// rather than a real <c>MaterialUsage</c> record. Edit / delete are not allowed
    /// on synthetic rows from the per-location panel — admin manages them via the
    /// Nákupy admin tab. <see cref="Id"/> on synthetic rows is the negated line id
    /// to keep ints disjoint from real usage ids; UI should use <see cref="FromPurchase"/>
    /// rather than the sign of Id when deciding what to show.
    /// </summary>
    public bool FromPurchase { get; set; } = false;
    public int? PurchaseId { get; set; }

    /// <summary>
    /// True when this row represents a service (rental etc.) rather than a
    /// physical material consumption. Drives the purple "Faktúra (služba)"
    /// badge on the Pracovisko view. False on manual entries and on rows
    /// minted from material invoice lines.
    /// </summary>
    public bool IsService { get; set; } = false;
}

public class CreateMaterialUsageDto
{
    [Required] public int MaterialId { get; set; }
    [Required, Range(0.001, 1_000_000)] public decimal Quantity { get; set; }
    [Required] public DateTime Date { get; set; }
    public int? EmployeeId { get; set; }
    [StringLength(2000)] public string? Note { get; set; }
    /// <summary>Optional override for the catalogue price snapshot. If null, the current
    /// <c>Material.PricePerUnit</c> is used. Used for retroactive entries where the
    /// price at that historical date is known.</summary>
    [Range(0, 1_000_000)] public decimal? UnitPriceAtTime { get; set; }
}

public class UpdateMaterialUsageDto
{
    [Required] public int MaterialId { get; set; }
    [Required, Range(0.001, 1_000_000)] public decimal Quantity { get; set; }
    [Required] public DateTime Date { get; set; }
    public int? EmployeeId { get; set; }
    [StringLength(2000)] public string? Note { get; set; }
    /// <summary>Optional override of the snapshot price for this entry. If null, the existing
    /// snapshot is preserved (inflation protection).</summary>
    [Range(0, 1_000_000)] public decimal? UnitPriceAtTime { get; set; }
}

public class MaterialSummaryRowDto
{
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal TotalQuantity { get; set; }
    /// <summary>Sum of (Quantity * UnitPriceAtTime) across all included usages, in EUR.</summary>
    public decimal TotalCost { get; set; }
    public int EntryCount { get; set; }
    public DateTime? LastEntryDate { get; set; }
}

// Reports
public class ReportFilterDto
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int? EmployeeId { get; set; }
    public int? LocationId { get; set; }
}

public class DailyReportDto
{
    public DateTime Date { get; set; }
    public List<DailyReportEntryDto> Entries { get; set; } = [];
    public double TotalHours { get; set; }
}

public class DailyReportEntryDto
{
    public string EmployeeName { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public string? CarName { get; set; }
    public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public double? HoursWorked { get; set; }
}

// Notifications
public class PushSubscribeDto
{
    [Required] public int EmployeeId { get; set; }
    [Required, StringLength(6, MinimumLength = 4)] public string Pin { get; set; } = string.Empty;
    [Required] public PushSubscriptionDto Subscription { get; set; } = null!;
}

public class PushSubscriptionDto
{
    [Required] public string Endpoint { get; set; } = string.Empty;
    [Required] public PushKeysDto Keys { get; set; } = null!;
    public string? UserAgent { get; set; }
}

public class PushKeysDto
{
    [Required, JsonPropertyName("p256dh")] public string P256dh { get; set; } = string.Empty;
    [Required] public string Auth { get; set; } = string.Empty;
}

public class PushUnsubscribeDto
{
    [Required] public string Endpoint { get; set; } = string.Empty;
}

public class VapidPublicKeyDto
{
    public string PublicKey { get; set; } = string.Empty;
}

public class NotificationConfigDto
{
    public bool NoActivity48hEnabled { get; set; }
    public string NoActivity48hTime { get; set; } = "18:00"; // HH:mm format
    public bool WorkingDaysOnly { get; set; }
    public bool ManagerSummaryEnabled { get; set; }
    public int? ManagerSummaryEmployeeId { get; set; }
}

public class UpdateNotificationConfigDto
{
    public bool? NoActivity48hEnabled { get; set; }
    public string? NoActivity48hTime { get; set; } // HH:mm format
    public bool? WorkingDaysOnly { get; set; }
    public bool? ManagerSummaryEnabled { get; set; }
    public int? ManagerSummaryEmployeeId { get; set; }
}

public class NotificationLogEntryDto
{
    public int Id { get; set; }
    public int? EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string TriggerDate { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public class NotificationEmployeeStatusDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public bool NotificationsEnabled { get; set; }
    public int PushSubscriptionCount { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
}

public class TestPushRequestDto
{
    [Required] public int EmployeeId { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
}

public class FireForEmployeeDto
{
    [Required] public int EmployeeId { get; set; }
    public bool IgnoreIdempotency { get; set; } = true;
}

public class NotificationTestResultDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
    public int? SendCount { get; set; }
}

// =================================================================
//  "Treba pripomenúť" — list of workers missing hours for last 2 days
// =================================================================
public class MissingHoursOverviewDto
{
    /// <summary>The dates that were checked (yyyy-MM-dd, local Bratislava). Excludes today.</summary>
    public List<string> CheckedDates { get; set; } = new();
    public List<EmployeeMissingDaysDto> Employees { get; set; } = new();
}

public class EmployeeMissingDaysDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    // PhoneNumber is intentionally NOT exposed here. This DTO is returned by the
    // anonymous kiosk endpoint /api/kiosk/missing-hours-overview, so anything on it
    // is publicly scrapeable. Phone numbers stay server-side; if a manager needs
    // them they look the worker up via the JWT-protected /api/employees admin API.
    /// <summary>Dates the worker has no time entry for (yyyy-MM-dd, local Bratislava).</summary>
    public List<string> MissingDates { get; set; } = new();
}

public class MyMissingDaysDto
{
    public string EmployeeName { get; set; } = string.Empty;
    public List<string> MissingDates { get; set; } = new();
}

// =================================================================
//  Admin variant of "Treba pripomenúť" — JWT-protected, includes
//  PhoneNumber so the manager can call/SMS workers from the
//  Notifikácie page. NEVER serve this from an anonymous endpoint.
// =================================================================
public class MissingHoursOverviewAdminDto
{
    public List<string> CheckedDates { get; set; } = new();
    public List<EmployeeMissingDaysAdminDto> Employees { get; set; } = new();
}

public class EmployeeMissingDaysAdminDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    /// <summary>
    /// Phone number is included here because this DTO is only ever returned
    /// by JWT-protected admin endpoints. If you find yourself adding it to
    /// an anonymous response you have made a mistake — use
    /// EmployeeMissingDaysDto (no phone) for the kiosk path instead.
    /// </summary>
    public string? PhoneNumber { get; set; }
    public List<string> MissingDates { get; set; } = new();
}

// =================================================================
//  Material purchases (kiosk + admin) — see MATERIAL_PURCHASES_PLAN.md.
//  Header + lines split mirrors the entity model. The kiosk POSTs the
//  whole receipt in one shot; the admin can edit lines individually.
// =================================================================

public class MaterialPurchaseLineDto
{
    public int Id { get; set; }
    /// <summary>Null while the line is "neidentifikovaný" (free-typed at the kiosk).</summary>
    public int? MaterialId { get; set; }
    /// <summary>Catalogue name if MaterialId is populated, otherwise null.</summary>
    public string? MaterialName { get; set; }
    /// <summary>Always populated. Survives admin renames so audit holds.</summary>
    public string MaterialNameRaw { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public class MaterialPurchaseDto
{
    public int Id { get; set; }
    public DateTime PurchaseDate { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public int? LocationId { get; set; }
    public string? LocationName { get; set; }
    public int? TimeEntryId { get; set; }
    /// <summary>Set when this purchase came from a scanned invoice — the UI
    /// then shows a "faktúra" badge instead of the placeholder employee.</summary>
    public int? InvoiceDocumentId { get; set; }
    public string? SupplierName { get; set; }
    public string? ReceiptPhotoUrl { get; set; }
    public string? Note { get; set; }
    public decimal TotalCost { get; set; }
    public List<MaterialPurchaseLineDto> Lines { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateMaterialPurchaseLineDto
{
    /// <summary>Optional. When null, this is a free-typed line and admin will promote it later.</summary>
    public int? MaterialId { get; set; }
    /// <summary>
    /// Required. When MaterialId is provided, this is normally a copy of the catalogue name
    /// (snapshotted at insert so admin renames don't rewrite this line). When MaterialId is null,
    /// this is the worker's free-typed name.
    /// </summary>
    [Required, StringLength(200)] public string MaterialNameRaw { get; set; } = string.Empty;
    [Required, StringLength(50)]  public string Unit { get; set; } = string.Empty;
    [Range(0.001, 1_000_000)]     public decimal Quantity { get; set; }
    [Range(0, 1_000_000)]         public decimal UnitPrice { get; set; }
}

/// <summary>
/// Kiosk-side create payload. The PIN identifies the buyer; no JWT.
/// EmployeeId is resolved server-side from the PIN.
/// </summary>
public class CreateKioskMaterialPurchaseDto
{
    [Required] public string Pin { get; set; } = string.Empty;
    /// <summary>Site the materials are for. Null = general / company stock.</summary>
    public int? LocationId { get; set; }
    /// <summary>
    /// Optional link back to the šichta TimeEntry created in the same kiosk
    /// session — populated by the in-šichta combined flow.
    /// </summary>
    public int? TimeEntryId { get; set; }
    [StringLength(200)] public string? SupplierName { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    /// <summary>At least one line required.</summary>
    [Required, MinLength(1)] public List<CreateMaterialPurchaseLineDto> Lines { get; set; } = new();
    /// <summary>
    /// Optional override of the purchase date. When null, server uses
    /// Europe/Bratislava "now". The kiosk normally lets the server timestamp.
    /// </summary>
    public DateTime? PurchaseDate { get; set; }
}

/// <summary>
/// Admin-side create payload. EmployeeId is explicit (the admin can backfill
/// purchases on behalf of a worker who forgot at the kiosk).
/// </summary>
public class CreateMaterialPurchaseDto
{
    [Required] public int EmployeeId { get; set; }
    public int? LocationId { get; set; }
    public int? TimeEntryId { get; set; }
    [StringLength(200)] public string? SupplierName { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    [Required] public DateTime PurchaseDate { get; set; }
    [Required, MinLength(1)] public List<CreateMaterialPurchaseLineDto> Lines { get; set; } = new();
}

/// <summary>
/// Replaces the header fields and the entire lines collection of an existing purchase.
/// Lines provided here REPLACE the existing list (so the admin can re-order, add, remove
/// in one shot from the edit drawer). Rows present in the request without an
/// <see cref="UpdateMaterialPurchaseLineDto.Id"/> are inserted; rows with a matching Id
/// are updated; existing rows not present are deleted.
/// </summary>
public class UpdateMaterialPurchaseDto
{
    public int? LocationId { get; set; }
    [StringLength(200)] public string? SupplierName { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    [Required] public DateTime PurchaseDate { get; set; }
    [Required, MinLength(1)] public List<UpdateMaterialPurchaseLineDto> Lines { get; set; } = new();
}

public class UpdateMaterialPurchaseLineDto
{
    /// <summary>Null = insert as new. Set = update existing row by id.</summary>
    public int? Id { get; set; }
    public int? MaterialId { get; set; }
    [Required, StringLength(200)] public string MaterialNameRaw { get; set; } = string.Empty;
    [Required, StringLength(50)]  public string Unit { get; set; } = string.Empty;
    [Range(0.001, 1_000_000)]     public decimal Quantity { get; set; }
    [Range(0, 1_000_000)]         public decimal UnitPrice { get; set; }
}

/// <summary>
/// Promote a free-typed line into the catalogue, OR merge it into an existing catalogue row.
/// Mode "new"   — creates a new <c>Material</c> from <see cref="NewName"/>/<see cref="NewUnit"/>
///                (and optional <see cref="NewPricePerUnit"/>); links this line to it.
/// Mode "merge" — links this line to an existing <see cref="CatalogueMaterialId"/>.
/// In both modes, when <see cref="ApplyToAllMatchingRawName"/> is true, the same MaterialId
/// is also stamped on every other line with the same case-insensitive
/// <c>MaterialNameRaw</c> + <c>Unit</c> across the whole table.
/// </summary>
public class PromoteMaterialLineDto
{
    /// <summary>"new" or "merge".</summary>
    [Required] public string Mode { get; set; } = "new";
    [StringLength(200)] public string? NewName { get; set; }
    [StringLength(50)]  public string? NewUnit { get; set; }
    [Range(0, 1_000_000)] public decimal? NewPricePerUnit { get; set; }
    public int? CatalogueMaterialId { get; set; }
    public bool ApplyToAllMatchingRawName { get; set; } = true;
}

public class MaterialPurchasePromoteResultDto
{
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    /// <summary>How many purchase lines ended up linked (this one + bulk-applied ones).</summary>
    public int LinesLinked { get; set; }
    public bool CreatedNewCatalogueRow { get; set; }
}

/// <summary>
/// One row in the "Neidentifikované" admin tab — groups orphan
/// (MaterialId == null) lines by case-insensitive raw name + unit so the
/// admin can promote a typo cluster in one click.
/// </summary>
public class UnknownMaterialGroupDto
{
    public string MaterialNameRaw { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public decimal TotalSpend { get; set; }
    /// <summary>Volume-weighted average paid price across the group. Suggested catalogue starting price.</summary>
    public decimal AverageUnitPrice { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public List<string> EnteredByEmployeeNames { get; set; } = new();
}

/// <summary>
/// Configuration echoed back to the kiosk so it knows whether to enable the
/// in-šichta combined flow. Returned from a small kiosk-side GET endpoint;
/// no PIN required because it is only metadata (the trigger Location id).
/// </summary>
public class MaterialPurchasesKioskConfigDto
{
    /// <summary>Location.Id that activates the in-šichta capture. 0 / null = no trigger configured.</summary>
    public int? TriggerLocationId { get; set; }
    /// <summary>Convenience — the Location's name, so the kiosk can build a banner without a second fetch.</summary>
    public string? TriggerLocationName { get; set; }
}

// ─── Payroll (Mzdy) — see PAYROLL_AND_PNL_PLAN.md. Admin-only. ────────

public class CreateEmployeeAdvanceDto
{
    [Required] public int EmployeeId { get; set; }
    [Required] public DateTime Date { get; set; }
    [Required, Range(0.01, 1_000_000)] public decimal Amount { get; set; }
    [StringLength(500)] public string? Note { get; set; }
}

public class UpdateEmployeeAdvanceDto
{
    [Required] public DateTime Date { get; set; }
    [Required, Range(0.01, 1_000_000)] public decimal Amount { get; set; }
    [StringLength(500)] public string? Note { get; set; }
}

public class EmployeeAdvanceDto
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One row in the monthly Mzdy summary table — one per employee.</summary>
public class PayrollRowDto
{
    public int EmployeeId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    /// <summary>Sum of hoursWorked across closed TimeEntries in the selected month.</summary>
    public decimal HoursWorked { get; set; }
    /// <summary>
    /// Weighted average of WageAtTime over the month —
    /// sum(WageAtTime * hours) / sum(hours). Null when sum(hours) == 0.
    /// </summary>
    public decimal? HourlyWageSnapshotAvg { get; set; }
    /// <summary>Current rate from Employee.HourlyWage. Null when unset.</summary>
    public decimal? HourlyWageCurrent { get; set; }
    /// <summary>True when every TimeEntry in the month carries WageAtTime = 0 (i.e. wage was unset at insert time).</summary>
    public bool WageMissing { get; set; }
    public decimal AdvancesTotal { get; set; }
    /// <summary>HoursWorked * HourlyWageSnapshotAvg.</summary>
    public decimal Gross { get; set; }
    /// <summary>Gross − AdvancesTotal.</summary>
    public decimal Payout { get; set; }
}

public class PayrollMonthlyDto
{
    /// <summary>YYYY-MM month identifier this view is for.</summary>
    public string Month { get; set; } = string.Empty;
    public List<PayrollRowDto> Rows { get; set; } = new();
    /// <summary>Sum of each Row column — for the table footer.</summary>
    public PayrollRowDto Totals { get; set; } = new();
}

/// <summary>
/// One month of the /admin/finance cost sparkline (GET /api/payroll/cost-trend).
/// Wages uses the same definition as PayrollMonthlyDto.Totals.Payout
/// (gross − advances) so the hero number and the trend agree.
/// </summary>
public class CostTrendMonthDto
{
    /// <summary>YYYY-MM.</summary>
    public string Month { get; set; } = string.Empty;
    public decimal Wages { get; set; }
    public decimal Material { get; set; }
    /// <summary>AZ Stroje division expense invoices — excluded from material
    /// views by design, so they are a separate cost pillar here.</summary>
    public decimal Stroje { get; set; }
    /// <summary>Výjazdy — distinct car+day rides × the live "vyjazd_auta"
    /// rate, same definition as the per-location P&L.</summary>
    public decimal Trips { get; set; }
    /// <summary>Odvody — worked hours × the sum of "€/h" CompanyRates rows
    /// (odvody, ubytovanie, customer-added), same add-on the P&L uses.</summary>
    public decimal Odvody { get; set; }
    /// <summary>All cost pillars: wages + material + stroje + trips + odvody.</summary>
    public decimal Total { get; set; }
    /// <summary>Income invoices (both divisions), by dátum dokladu.</summary>
    public decimal Income { get; set; }
    /// <summary>DPH inside the month's expense documents (faktúry + bločky,
    /// both divisions) — what a VAT payer could reclaim.</summary>
    public decimal Vat { get; set; }
}

// ─── Per-location P&L (Náklady a zisk) — PAYROLL_AND_PNL_PLAN.md ───
// Admin-only data (wages + contract values); MUST NOT be reused by any
// /api/kiosk/* endpoint.

public class UpdateContractValueDto
{
    /// <summary>EUR. Null clears the contract value.</summary>
    public decimal? ContractValue { get; set; }
}

public class LocationPnlDto
{
    public PnlLocationDto Location { get; set; } = new();
    public PnlLabourDto Labour { get; set; } = new();
    /// <summary>Null when the MaterialPurchases flag is off for the caller.</summary>
    public PnlMaterialDto? Material { get; set; }
    /// <summary>Invoice money assigned to this location in the range, s DPH,
    /// any document status except discarded (unlike Material, which counts
    /// committed usages only). Computed by the pnl-summary endpoint; null on
    /// the single-location P&amp;L.</summary>
    public decimal? InvoicedInclVat { get; set; }
    /// <summary>Car trips to the site (F5) — null when no car was used in the range.</summary>
    public PnlTripsDto? Trips { get; set; }
    /// <summary>= Location.ContractValue. Null when no contract is recorded.</summary>
    public decimal? Revenue { get; set; }
    /// <summary>Revenue − Labour.Cost − Material.Cost − Trips.Cost. Null when Revenue is null.</summary>
    public decimal? Profit { get; set; }
}

/// <summary>Výjazdy (F5): one ride per car per calendar day, no matter how
/// many workers shared the vehicle. Priced at the current "vyjazd_auta"
/// rate from Odvody.</summary>
public class PnlTripsDto
{
    public int Count { get; set; }
    /// <summary>EUR per výjazd at report time.</summary>
    public decimal Rate { get; set; }
    public decimal Cost { get; set; }
}

public class PnlLocationDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? ContractValue { get; set; }
    /// <summary>False for deactivated sites — the report still shows them
    /// (with their historical costs) when the range covers their activity.</summary>
    public bool IsActive { get; set; } = true;
}

public class PnlLabourDto
{
    public decimal HoursWorked { get; set; }
    /// <summary>Sum of hours × TimeEntry.WageAtTime snapshots (never current wages).</summary>
    public decimal Cost { get; set; }
    public List<PnlLabourRowDto> BreakdownByEmployee { get; set; } = new();
}

public class PnlLabourRowDto
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    /// <summary>Weighted average WageAtTime over the range. Null when Hours == 0.</summary>
    public decimal? AvgWage { get; set; }
    public decimal Cost { get; set; }
}

public class PnlMaterialDto
{
    /// <summary>Sum of quantity × UnitPriceAtTime snapshots (never current catalogue prices).</summary>
    public decimal Cost { get; set; }
    public List<PnlMaterialRowDto> BreakdownByMaterial { get; set; } = new();
}

public class PnlMaterialRowDto
{
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    /// <summary>Cost / Quantity. Null when Quantity == 0.</summary>
    public decimal? AvgUnitPrice { get; set; }
    public decimal Cost { get; set; }
}


