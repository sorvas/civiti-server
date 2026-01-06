using System.ComponentModel.DataAnnotations;
using Civica.Api.Models.Domain;

namespace Civica.Api.Models.Requests.Issues;

/// <summary>
/// Request model for updating an issue's status.
/// Users can only change status of their own issues.
/// </summary>
public class UpdateIssueStatusRequest
{
    /// <summary>
    /// The new status for the issue.
    /// Users can set: Cancelled, Resolved
    /// </summary>
    [Required]
    public IssueStatus Status { get; set; }
}
