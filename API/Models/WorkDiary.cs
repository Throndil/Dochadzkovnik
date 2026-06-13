namespace API.Models;

/// <summary>
/// A standalone stavebný denník (construction diary) entry uploaded by a worker
/// via the kiosk. Sibling of <see cref="WorkPhoto"/>: both are proof-of-work
/// types that can be attached to a day at a Location, with an optional link
/// back to the <see cref="TimeEntry"/> they were submitted alongside.
///
/// Unlike WorkPhoto, the body is mandatory text (the diary entry itself);
/// the attachment (PDF or image of the physical diary page) is optional.
/// See PROOF_OF_WORK_UX_PLAN.md §Schema for the design rationale.
/// </summary>
public class WorkDiary
{
    public int Id { get; set; }

    /// <summary>Worker who wrote the diary. Null = admin-uploaded on behalf of a worker.</summary>
    public int? EmployeeId { get; set; }

    public int LocationId { get; set; }

    /// <summary>
    /// Populated when the diary is submitted in the same kiosk session as an
    /// hours entry. Null when standalone. SetNull on TimeEntry delete so the
    /// diary survives a hours-entry deletion.
    /// </summary>
    public int? TimeEntryId { get; set; }

    /// <summary>Day the work happened. Stored as midnight UTC by convention.</summary>
    public DateTime Date { get; set; }

    /// <summary>The free-form diary entry. Required.</summary>
    public string BodyText { get; set; } = string.Empty;

    /// <summary>Optional PDF or image scan of the physical diary page.</summary>
    public string? AttachmentUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Employee? Employee { get; set; }
    public Location Location { get; set; } = null!;
    public TimeEntry? TimeEntry { get; set; }
}
