using Civiti.Domain.Attributes;
using Civiti.Domain.Entities;
using Civiti.Application.Responses.Authority;

namespace Civiti.Application.Responses.Issues;

public class IssueDetailResponse
{
    public Guid Id { get; set; }
    [Untrusted] public string Title { get; set; } = string.Empty;
    [Untrusted] public string Description { get; set; } = string.Empty;
    public IssueCategory Category { get; set; }
    [Untrusted] public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? District { get; set; }
    public UrgencyLevel Urgency { get; set; }
    public IssueStatus Status { get; set; }
    public int EmailsSent { get; set; }
    public int CommunityVotes { get; set; }
    public bool? HasVoted { get; set; }
    [Untrusted] public string? DesiredOutcome { get; set; }
    [Untrusted] public string? CommunityImpact { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// When the issue entered the Resolved state it is in now; <c>null</c> for anything not
    /// currently Resolved. Not <see cref="UpdatedAt"/>, which a later edit or vote also moves.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    // Related data
    public List<IssuePhotoResponse> Photos { get; set; } = [];

    /// <summary>
    /// The author's "after" photos for the current resolution, in the order they arranged them.
    /// Empty unless <see cref="Status"/> is <see cref="IssueStatus.Resolved"/>, and empty for a
    /// resolution the author chose not to photograph — attaching proof is optional.
    /// </summary>
    public List<IssueResolutionPhotoResponse> ResolutionPhotos { get; set; } = [];

    public List<IssueAuthorityResponse> Authorities { get; set; } = [];
    public UserBasicResponse User { get; set; } = null!;
}

/// <summary>
/// An "after" photo attached when the author resolved the issue. Deliberately not an
/// <see cref="IssuePhotoResponse"/>: <c>IsPrimary</c> carries no meaning in a resolution set,
/// and emitting a field that is always false invites clients to build on it.
/// </summary>
public class IssueResolutionPhotoResponse
{
    public Guid Id { get; set; }

    /// <summary>
    /// Author-supplied and echoed verbatim, so it is marked untrusted even though it is only a
    /// URL: up to 1000 characters of chosen path and query survive the scheme check, and an MCP
    /// client renders this string into a model's context.
    /// </summary>
    [Untrusted] public string Url { get; set; } = string.Empty;

    [Untrusted] public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class IssuePhotoResponse
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    [Untrusted] public string? Description { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserBasicResponse
{
    /// <summary>
    /// The creator's Supabase auth id (the JWT <c>sub</c>) — the same identifier the caller
    /// holds for itself, so a client can compare it to decide ownership. For a deleted creator it
    /// is the all-zeros sentinel (<c>00000000-0000-0000-0000-000000000000</c>), which matches no
    /// caller. This is deliberately not the internal <c>UserProfile.Id</c> PK, which no client can
    /// match against its own identity.
    /// </summary>
    public string Id { get; set; } = string.Empty;
    [Untrusted] public string Name { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
}