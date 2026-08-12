namespace Civiti.Domain.Entities;

/// <summary>
/// An "after" photo the author attached when marking their issue resolved — proof that the
/// thing they reported actually got fixed.
/// <para>
/// A separate entity from <see cref="IssuePhoto"/> rather than a discriminated row in the same
/// table, because <see cref="Issue.Photos"/> is read by a dozen call sites that all mean "the
/// photos of the problem": the owner edit replaces that collection wholesale, the list responses
/// pick a thumbnail out of it, the quality-photos badge counts it, and the approved-content
/// snapshot diffs it. Resolution photos are none of those things, and folding them in would have
/// meant filtering every one of those sites correctly and forever. Keeping the collections apart
/// makes the separation structural instead of a convention.
/// </para>
/// <para>
/// The set exists if and only if the issue is currently <see cref="IssueStatus.Resolved"/>:
/// resolving replaces it, re-opening deletes it. See
/// <c>IssueService.UpdateIssueStatusAsync</c>.
/// </para>
/// </summary>
public class IssueResolutionPhoto
{
    public Guid Id { get; set; }
    public Guid IssueId { get; set; }
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional author-supplied caption. Untrusted text — echoed to clients as-is.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Position within the resolution set, as the author arranged it. Stored rather than
    /// inferred for the same reason as <see cref="IssuePhoto.DisplayOrder"/>: the whole set is
    /// written in one go, so <see cref="CreatedAt"/> ties and the id tiebreak is random.
    /// </summary>
    public int DisplayOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Issue Issue { get; set; } = null!;
}
