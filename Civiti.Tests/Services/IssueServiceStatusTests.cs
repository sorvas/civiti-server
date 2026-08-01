using Civiti.Application.Requests.Issues;
using Civiti.Application.Responses.Moderation;
using Civiti.Application.Services;
using Civiti.Domain.Entities;
using Civiti.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace Civiti.Tests.Services;

/// <summary>
/// Owner-driven status changes (<c>PUT /api/user/issues/{id}/status</c>).
/// <para>
/// This is the other door into public visibility, and it has to be held to the same standard as
/// the edit path: <see cref="IssueStatus.Resolved"/> is publicly viewable, so anything that can
/// reach it can publish content.
/// </para>
/// </summary>
public class IssueServiceStatusTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IGamificationService> _gamificationService = new();
    private readonly Mock<IActivityService> _activityService = new();
    private readonly Mock<INotificationService> _notificationService = new();
    private readonly Mock<IAdminNotifier> _adminNotifier = new();
    private readonly Mock<IContentModerationService> _contentModerationService = new();
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());

    public IssueServiceStatusTests()
    {
        _contentModerationService
            .Setup(m => m.ModerateContentAsync(It.IsAny<string>()))
            .ReturnsAsync(new ContentModerationResponse { IsAllowed = true });
    }

    private Infrastructure.Services.IssueService CreateService() => new(
        Mock.Of<ILogger<Infrastructure.Services.IssueService>>(), _dbFactory.CreateContext(),
        _gamificationService.Object, _memoryCache,
        _activityService.Object, _notificationService.Object,
        _adminNotifier.Object, _contentModerationService.Object);

    public void Dispose()
    {
        _memoryCache.Dispose();
        _dbFactory.Dispose();
    }

    private async Task<(UserProfile Owner, Issue Issue)> SeedAsync(IssueStatus status)
    {
        var owner = TestDataBuilder.CreateUser();
        var issue = TestDataBuilder.CreateIssue(userId: owner.Id, status: status);

        using var ctx = _dbFactory.CreateContext();
        ctx.UserProfiles.Add(owner);
        ctx.Issues.Add(issue);
        await ctx.SaveChangesAsync();

        return (owner, await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id));
    }

    [Theory]
    [InlineData(IssueStatus.Submitted)]
    [InlineData(IssueStatus.UnderReview)]
    [InlineData(IssueStatus.Rejected)]
    [InlineData(IssueStatus.Draft)]
    public async Task Owner_Should_Not_Be_Able_To_Publish_Unreviewed_Content_By_Resolving_It(
        IssueStatus neverApprovedStatus)
    {
        // Resolved is publicly viewable. If an owner can move an issue there from a status that
        // no admin has approved, they can publish whatever they like without review — and every
        // other moderation guard becomes theatre.
        var (owner, issue) = await SeedAsync(neverApprovedStatus);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId);

        success.Should().BeFalse(
            "an issue that was never approved must not be publishable by its own author");
        error.Should().NotBeNull();

        using var ctx = _dbFactory.CreateContext();
        Issue stored = await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id);
        stored.Status.Should().Be(neverApprovedStatus);
    }

    [Fact]
    public async Task Owner_Should_Not_Be_Able_To_Republish_An_Edited_Issue_By_Resolving_It()
    {
        // The bypass that would defeat the re-review diff: edit a live issue so it drops out of
        // public view pending re-approval, then resolve it straight back into public view with
        // the unreviewed content and every supporter counter intact.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        var edit = new UpdateIssueRequest
        {
            Title = "Swapped content",
            Description = "Replaced after approval, never re-reviewed",
            Category = IssueCategory.Other,
            Address = issue.Address,
            District = issue.District ?? "Sector 1",
            Latitude = issue.Latitude,
            Longitude = issue.Longitude,
            Resubmit = true,
            ExpectedUpdatedAt = issue.UpdatedAt
        };

        (await CreateService().UpdateIssueAsync(issue.Id, edit, owner.SupabaseUserId))
            .Outcome.Should().Be(UpdateIssueOutcome.Success);

        var (success, _) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId);

        success.Should().BeFalse("resolving must not be a way around re-review");

        var publicList = await CreateService().GetAllIssuesAsync(
            new GetIssuesRequest { Page = 1, PageSize = 50, Statuses = [IssueStatus.Active, IssueStatus.Resolved] });
        publicList.Items.Should().NotContain(i => i.Id == issue.Id,
            "the edited content must stay out of public view until an admin re-approves it");

        (await CreateService().GetIssueByIdAsync(issue.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Owner_Should_Still_Be_Able_To_Resolve_A_Live_Issue()
    {
        // The legitimate case the endpoint exists for: the authority fixed the problem.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId);

        success.Should().BeTrue(error);

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Resolved);
    }

    [Theory]
    [InlineData(IssueStatus.Submitted)]
    [InlineData(IssueStatus.UnderReview)]
    [InlineData(IssueStatus.Rejected)]
    [InlineData(IssueStatus.Active)]
    public async Task Owner_Should_Still_Be_Able_To_Cancel_At_Any_Stage(IssueStatus from)
    {
        // Withdrawing your own submission is legitimate whatever stage it is at, and Cancelled
        // is not publicly viewable, so it carries none of the same risk.
        var (owner, issue) = await SeedAsync(from);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Cancelled },
            owner.SupabaseUserId);

        success.Should().BeTrue(error);
    }

    [Fact]
    public async Task Owner_Should_Be_Able_To_Reopen_A_Resolved_Issue()
    {
        // Re-opening is the one exit from Resolved, and it is safe where a Cancelled one would not
        // be: Resolved is only reachable from Active, and a resolved issue is not owner-editable,
        // so the content was approved by an admin and cannot have changed since.
        var (owner, issue) = await SeedAsync(IssueStatus.Resolved);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId);

        success.Should().BeTrue(error);

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Active);

        (await CreateService().GetIssueByIdAsync(issue.Id))
            .Should().NotBeNull("a re-opened issue returns to public view, as it was before");
    }

    [Fact]
    public async Task Owner_Should_Not_Be_Able_To_Reopen_A_Cancelled_Issue()
    {
        // Cancelled stays absorbing. Unlike Resolved it is reachable from Submitted, UnderReview
        // and Rejected, so an exit into Active would publish content no admin ever approved.
        var (owner, issue) = await SeedAsync(IssueStatus.Cancelled);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId);

        success.Should().BeFalse("cancelled is terminal — re-opening it would bypass review");
        error.Should().NotBeNull();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Cancelled);
    }

    [Theory]
    [InlineData(IssueStatus.Submitted)]
    [InlineData(IssueStatus.UnderReview)]
    [InlineData(IssueStatus.Rejected)]
    [InlineData(IssueStatus.Draft)]
    public async Task Owner_Should_Not_Be_Able_To_Publish_Unreviewed_Content_By_Activating_It(
        IssueStatus neverApprovedStatus)
    {
        // The mirror of the resolve guard. Active is publicly viewable too, so allowing owners to
        // set it from anywhere except Resolved would reopen the exact hole the resolve guard shut.
        // Admin approval must remain the only way an issue first goes live.
        var (owner, issue) = await SeedAsync(neverApprovedStatus);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId);

        success.Should().BeFalse(
            "only a resolved issue may be re-opened — never one that was never approved");
        error.Should().NotBeNull();

        using var ctx = _dbFactory.CreateContext();
        Issue stored = await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id);
        stored.Status.Should().Be(neverApprovedStatus);
    }

    [Fact]
    public async Task Reopening_And_Reresolving_Should_Award_Points_Only_Once()
    {
        // Resolve/re-open/resolve is a loop, so the reward has to be once per issue rather than
        // once per resolution. Badges and achievement progress cannot be cleanly un-awarded, which
        // is why the guard is a one-way stamp rather than a reversal on re-open.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);
        var request = new UpdateIssueStatusRequest { Status = IssueStatus.Resolved };

        (await CreateService().UpdateIssueStatusAsync(issue.Id, request, owner.SupabaseUserId))
            .Success.Should().BeTrue();

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        (await CreateService().UpdateIssueStatusAsync(issue.Id, request, owner.SupabaseUserId))
            .Success.Should().BeTrue();

        _gamificationService.Verify(
            g => g.AwardPointsAsync(owner.Id, 100, "Issue resolved", It.IsAny<bool>()),
            Times.Once);
        _gamificationService.Verify(
            g => g.UpdateAchievementProgressAsync(
                owner.Id, "issues_resolved", It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task Reopening_Should_Decrement_The_Resolved_Count()
    {
        // IssuesResolved describes the issue's current state, so unlike the reward it moves both
        // ways — otherwise a re-opened issue would keep inflating the owner's resolved tally.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using (var ctx = _dbFactory.CreateContext())
        {
            (await ctx.UserProfiles.AsNoTracking().FirstAsync(u => u.Id == owner.Id))
                .IssuesResolved.Should().Be(1);
        }

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var after = _dbFactory.CreateContext();
        (await after.UserProfiles.AsNoTracking().FirstAsync(u => u.Id == owner.Id))
            .IssuesResolved.Should().Be(0);
    }

    [Fact]
    public async Task Reopening_An_Issue_Resolved_Before_This_Feature_Should_Not_Go_Negative()
    {
        // ResolutionRewardedAt is null and IssuesResolved is 0 for issues resolved before the
        // counter could be decremented, so the floor is what stops the tally going below zero.
        var (owner, issue) = await SeedAsync(IssueStatus.Resolved);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.UserProfiles.AsNoTracking().FirstAsync(u => u.Id == owner.Id))
            .IssuesResolved.Should().Be(0);
    }

    [Fact]
    public async Task A_Stranger_Should_Not_Be_Able_To_Change_Someone_Elses_Status()
    {
        var (_, issue) = await SeedAsync(IssueStatus.Active);
        var stranger = TestDataBuilder.CreateUser();
        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(stranger);
            await ctx.SaveChangesAsync();
        }

        var (success, _) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            stranger.SupabaseUserId);

        success.Should().BeFalse();
    }
}
