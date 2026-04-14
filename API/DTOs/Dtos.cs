using System.ComponentModel.DataAnnotations;

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
}

public class WeeklyRowDto
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public List<WeeklyDayDto> Days { get; set; } = [];
    public double TotalHours { get; set; }
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
