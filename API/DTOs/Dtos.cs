using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace API.DTOs;

// Auth
public class LoginDto
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

public class AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
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
public class CreateTimeEntryDto
{
    [Required] public int EmployeeId { get; set; }
    [Required] public int LocationId { get; set; }
    public int? CarId { get; set; }
    [Required] public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public string? Note { get; set; }
}

public class UpdateTimeEntryDto
{
    public int? CarId { get; set; }
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
    public DateTime ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public double? HoursWorked { get; set; }
    public string? Note { get; set; }
    public string? PhotoUrl { get; set; }
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
    public string? Note { get; set; }
    public DateTime? Date { get; set; }
}

// Car
public class CarDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LicensePlate { get; set; }
    public string? PhotoUrl { get; set; }
    public bool IsActive { get; set; }
}

public class CreateCarDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(20)] public string? LicensePlate { get; set; }
}

public class UpdateCarDto
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(20)] public string? LicensePlate { get; set; }
    public bool IsActive { get; set; } = true;
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
    public string? PhoneNumber { get; set; }
    /// <summary>Dates the worker has no time entry for (yyyy-MM-dd, local Bratislava).</summary>
    public List<string> MissingDates { get; set; } = new();
}

public class MyMissingDaysDto
{
    public string EmployeeName { get; set; } = string.Empty;
    public List<string> MissingDates { get; set; } = new();
}

