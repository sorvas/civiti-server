using Civiti.Domain.Constants;
using Civiti.Domain.Entities;
using Civiti.Domain.Exceptions;

namespace Civiti.Infrastructure.Services.Issues;

/// <summary>
/// Turns the client-supplied "after" photo URLs on a resolve into
/// <see cref="IssueResolutionPhoto"/> rows.
/// </summary>
internal static class IssueResolutionPhotoWriter
{
    /// <summary>
    /// Rejects a resolution photo set that is unsafe or oversized.
    /// <para>
    /// The URL guard is <see cref="IssuePhotoWriter.ValidateUrls"/> itself rather than a copy of
    /// it: these URLs are echoed back in <c>IssueDetailResponse.ResolutionPhotos</c> and rendered
    /// by the same clients, so a <c>javascript:</c> or <c>data:</c> URI is exactly as dangerous
    /// here as it is on an issue photo. Sharing the guard is what stops the two paths drifting.
    /// </para>
    /// </summary>
    /// <exception cref="IssueContentValidationException">
    /// More than <see cref="IssueValidationLimits.MaxResolutionPhotoCount"/> URLs survive the
    /// blank filter, or one of them is over-length or not http(s).
    /// </exception>
    public static void Validate(IEnumerable<string>? photoUrls)
    {
        if (photoUrls is null)
        {
            return;
        }

        // Counted after dropping blanks, matching what Materialize will actually write: a
        // trailing empty slot from a client's fixed-size picker is not a fourth photo.
        var meaningful = photoUrls.Count(url => !string.IsNullOrWhiteSpace(url));
        if (meaningful > IssueValidationLimits.MaxResolutionPhotoCount)
        {
            throw new IssueContentValidationException(
                $"An issue can carry at most {IssueValidationLimits.MaxResolutionPhotoCount} resolution photos.");
        }

        IssuePhotoWriter.ValidateUrls(photoUrls);
    }

    /// <summary>
    /// Builds the resolution photo rows for an issue. Blank entries are dropped and the
    /// surviving order is preserved as <see cref="IssueResolutionPhoto.DisplayOrder"/>.
    /// Call <see cref="Validate"/> first.
    /// </summary>
    public static List<IssueResolutionPhoto> Materialize(
        Guid issueId,
        IEnumerable<string>? photoUrls,
        DateTime timestamp) =>
        (photoUrls ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select((url, index) => new IssueResolutionPhoto
            {
                Id = Guid.NewGuid(),
                IssueId = issueId,
                Url = url,
                DisplayOrder = index,
                CreatedAt = timestamp
            })
            .ToList();
}
