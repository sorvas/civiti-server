using Civica.Api.Models.Responses.Issues;
using Civica.Api.Models.Responses.Common;
using Civica.Api.Models.Requests.Issues;

namespace Civica.Api.Services.Interfaces;

public interface IIssueService
{
    Task<PagedResult<IssueListResponse>> GetAllIssuesAsync(GetIssuesRequest request);
    Task<IssueDetailResponse?> GetIssueByIdAsync(Guid id);
    Task<CreateIssueResponse> CreateIssueAsync(CreateIssueRequest request, string supabaseUserId);
    Task<(bool Success, string? Error)> IncrementEmailCountAsync(Guid issueId, string? clientIp);
    Task<PagedResult<IssueListResponse>> GetUserIssuesAsync(string supabaseUserId, GetUserIssuesRequest request);

    /// <summary>
    /// Update an issue's status (user can only change status of their own issues, unless admin).
    /// Allowed user transitions: Cancelled, Resolved
    /// </summary>
    Task<(bool Success, string? Error)> UpdateIssueStatusAsync(Guid issueId, UpdateIssueStatusRequest request, string supabaseUserId, bool isAdmin = false);

    /// <summary>
    /// Update an issue (user can only edit their own issues, except Cancelled/Resolved).
    /// After update, status is set to UnderReview.
    /// </summary>
    Task<(bool Success, IssueDetailResponse? Issue, string? Error)> UpdateIssueAsync(
        Guid issueId,
        UpdateIssueRequest request,
        string supabaseUserId);
}