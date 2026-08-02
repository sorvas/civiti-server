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
}
