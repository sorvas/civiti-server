using System.ComponentModel.DataAnnotations;
using Civiti.Domain.Entities;

namespace Civiti.Application.Requests.Issues;

/// <summary>
/// Request model for updating an issue's status.
/// Users can only change status of their own issues.
/// </summary>
public class UpdateIssueStatusRequest
{
    /// <summary>
    /// The new status for the issue.
    /// Users can set: Cancelled (from any non-terminal status), Resolved (only from Active),
    /// Active (only from Resolved — re-opening an issue they had resolved).
    /// </summary>
    [Required]
    public IssueStatus Status { get; set; }

    /// <summary>
    /// Optional "after" photos proving the issue was fixed, accepted only when
    /// <see cref="Status"/> is <see cref="IssueStatus.Resolved"/>. Sending them alongside any
    /// other status is rejected rather than ignored — silently dropping them would leave a
    /// client believing it had attached proof that nobody will ever see.
    /// <para>
    /// Replace-set semantics: every resolve rewrites the whole set, and omitting the field — or
    /// sending an empty list — resolves with no photos at all. What is displayed is therefore
    /// always what the *latest* resolve attached, so a resolve-with-proof / re-open /
    /// resolve-without-proof sequence cannot leave stale proof on the page. Re-opening deletes
    /// the set outright, which is what makes "resolution photos exist only while Resolved" an
    /// invariant the clients can rely on rather than a convention.
    /// </para>
    /// </summary>
    public List<string>? ResolutionPhotoUrls { get; set; }
}
