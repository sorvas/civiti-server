using Civiti.Application.Requests.Issues;
using Civiti.Application.Responses.Moderation;
using Civiti.Application.Services;
using Civiti.Domain.Constants;
using Civiti.Domain.Entities;
using Civiti.Tests.Helpers;
using FluentAssertions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    private Infrastructure.Services.IssueService CreateService(params IInterceptor[] interceptors) => new(
        Mock.Of<ILogger<Infrastructure.Services.IssueService>>(), _dbFactory.CreateContext(interceptors),
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

    [Fact]
    public async Task Reresolving_Should_Not_Blast_Supporters_A_Second_Time()
    {
        // Resolving fans out a push and an email to every voter and commenter. Re-open made that
        // loop repeatable, so without a guard an author could mail everyone following their issue
        // on every lap — real spend, and a deliverability hit for the whole domain.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);
        var resolve = new UpdateIssueStatusRequest { Status = IssueStatus.Resolved };
        var reopen = new UpdateIssueStatusRequest { Status = IssueStatus.Active };

        for (var lap = 0; lap < 3; lap++)
        {
            (await CreateService().UpdateIssueStatusAsync(issue.Id, resolve, owner.SupabaseUserId))
                .Success.Should().BeTrue();
            (await CreateService().UpdateIssueStatusAsync(issue.Id, reopen, owner.SupabaseUserId))
                .Success.Should().BeTrue();
        }

        _notificationService.Verify(
            n => n.NotifyIssueResolvedAsync(
                It.IsAny<Issue>(), It.IsAny<UserProfile>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "supporters hear about a resolution once per cooldown, however many laps the author runs");
    }

    [Fact]
    public async Task Reresolving_After_The_Cooldown_Should_Reach_Supporters_Again()
    {
        // The other half of the argument: this is a cooldown, not a latch. An issue that genuinely
        // comes back and is genuinely fixed again has to be able to tell the people following it,
        // otherwise the anti-spam guard swallows the notification that matters most.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);
        var resolve = new UpdateIssueStatusRequest { Status = IssueStatus.Resolved };
        var reopen = new UpdateIssueStatusRequest { Status = IssueStatus.Active };

        (await CreateService().UpdateIssueStatusAsync(issue.Id, resolve, owner.SupabaseUserId))
            .Success.Should().BeTrue();
        (await CreateService().UpdateIssueStatusAsync(issue.Id, reopen, owner.SupabaseUserId))
            .Success.Should().BeTrue();

        // Age the stamp past the cooldown rather than waiting out a day.
        using (var ctx = _dbFactory.CreateContext())
        {
            Issue stored = await ctx.Issues.FirstAsync(i => i.Id == issue.Id);
            stored.ResolutionNotifiedAt = DateTime.UtcNow - TimeSpan.FromHours(25);
            await ctx.SaveChangesAsync();
        }

        (await CreateService().UpdateIssueStatusAsync(issue.Id, resolve, owner.SupabaseUserId))
            .Success.Should().BeTrue();

        _notificationService.Verify(
            n => n.NotifyIssueResolvedAsync(
                It.IsAny<Issue>(), It.IsAny<UserProfile>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // The reward latch and the announcement cooldown are separate guards with separate
        // predicates, and only this assertion stops one being copy-pasted over the other:
        // an expiring cooldown must re-arm the announcement without re-arming the payout.
        _gamificationService.Verify(
            g => g.AwardPointsAsync(owner.Id, 100, "Issue resolved", It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task Reresolving_Inside_The_Cooldown_Should_Still_Succeed()
    {
        // Suppressing the fan-out must not turn into suppressing the transition: the issue still
        // has to end up Resolved and publicly visible.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);
        var resolve = new UpdateIssueStatusRequest { Status = IssueStatus.Resolved };

        (await CreateService().UpdateIssueStatusAsync(issue.Id, resolve, owner.SupabaseUserId))
            .Success.Should().BeTrue();
        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id, resolve, owner.SupabaseUserId);

        success.Should().BeTrue();
        error.Should().BeNull();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Resolved);
    }

    [Fact]
    public async Task A_Transition_Lost_To_A_Concurrent_Request_Should_Change_Nothing()
    {
        // The claim exists because ValidateStatusTransition runs against a read taken without a
        // row lock: two concurrent resolves both pass it, and the reward is not idempotent.
        //
        // This simulates that interleaving rather than reproducing it — every context here shares
        // one SQLite connection, so there is no second transaction to race, and SQLite has none of
        // the row-lock semantics that make the real loser block and re-evaluate. What it does pin
        // is the branch that matters: when the claim matches no rows, nothing is awarded, nothing
        // is mailed, and the caller is told to reload. A guard derived from the snapshot rather
        // than from rows-affected fails this test.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        // No WHERE clause: SeedAsync creates exactly one issue, and a Guid literal would depend on
        // how the SQLite provider happens to store it.
        var rival = new RivalTransitionInterceptor(
            $"""UPDATE "Issues" SET "Status" = {(int)IssueStatus.Resolved}""");

        var (success, error) = await CreateService(rival).UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId);

        rival.RowsStolen.Should().Be(1, "the test is meaningless unless the rival really moved the row");
        success.Should().BeFalse();
        error.Should().Be(DomainErrors.IssueStatusConflict);

        _gamificationService.Verify(
            g => g.AwardPointsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        _notificationService.Verify(
            n => n.NotifyIssueResolvedAsync(
                It.IsAny<Issue>(), It.IsAny<UserProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);

        using var ctx = _dbFactory.CreateContext();
        (await ctx.UserProfiles.AsNoTracking().FirstAsync(u => u.Id == owner.Id))
            .IssuesResolved.Should().Be(0, "the losing request must not count a resolution");
    }

    [Fact]
    public async Task Resolving_An_Issue_Whose_Author_Deleted_Their_Account_Should_Not_Touch_Their_Stats()
    {
        // The counter moves in SQL now rather than through a loaded entity, so the "owner is gone"
        // case is carried by the global query filter instead of by a null check. Same outcome —
        // an anonymised profile no longer participates in gamification — but it is worth pinning,
        // because the filter doing that work is invisible at the call site.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);
        var admin = TestDataBuilder.CreateUser();

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(admin);
            UserProfile stored = await ctx.UserProfiles.FirstAsync(u => u.Id == owner.Id);
            stored.IsDeleted = true;
            await ctx.SaveChangesAsync();
        }

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            admin.SupabaseUserId,
            isAdmin: true)).Success.Should().BeTrue();

        using var after = _dbFactory.CreateContext();
        UserProfile deletedOwner = await after.UserProfiles
            .IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Id == owner.Id);
        deletedOwner.IssuesResolved.Should().Be(0);
    }
    [Fact]
    public async Task Self_Resolving_Should_Award_A_Resolution_Badge_On_The_Resolution_That_Earns_It()
    {
        // Every other test here mocks IGamificationService, which is exactly why this class of bug
        // survives: IssuesResolved moves in SQL now, and the badge threshold is judged against a
        // UserProfile the caller already had tracked. In production IIssueService and
        // IGamificationService are both scoped over one DbContext, so EF hands the badge check that
        // same instance — stale, unless it is reloaded. Wire the real service to pin it.
        var owner = TestDataBuilder.CreateUser();
        var issue = TestDataBuilder.CreateIssue(userId: owner.Id, status: IssueStatus.Active);
        var badge = TestDataBuilder.CreateBadge(
            name: "Problem Solver", requirementType: "issues_resolved", requirementValue: 1);

        using (var seed = _dbFactory.CreateContext())
        {
            seed.UserProfiles.Add(owner);
            seed.Issues.Add(issue);
            seed.Badges.Add(badge);
            await seed.SaveChangesAsync();
        }

        // One shared context across both services, mirroring the scoped DI registration.
        using var shared = _dbFactory.CreateContext();
        var gamification = new Infrastructure.Services.GamificationService(
            Mock.Of<ILogger<Infrastructure.Services.GamificationService>>(),
            shared,
            _notificationService.Object);
        var service = new Infrastructure.Services.IssueService(
            Mock.Of<ILogger<Infrastructure.Services.IssueService>>(), shared,
            gamification, _memoryCache,
            _activityService.Object, _notificationService.Object,
            _adminNotifier.Object, _contentModerationService.Object);

        (await service.UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var after = _dbFactory.CreateContext();
        (await after.UserProfiles.AsNoTracking().FirstAsync(u => u.Id == owner.Id))
            .IssuesResolved.Should().Be(1);
        (await after.UserBadges.AsNoTracking().ToListAsync())
            .Should().ContainSingle(ub => ub.UserId == owner.Id && ub.BadgeId == badge.Id,
                "the badge is earned by this resolution, not by the next one");
    }

    [Fact]
    public async Task Resolving_Should_Store_The_Proof_Photos_In_The_Order_They_Were_Sent()
    {
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls = ["https://cdn.test/after-1.jpg", "https://cdn.test/after-2.jpg"]
            },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var ctx = _dbFactory.CreateContext();
        List<IssueResolutionPhoto> stored = await ctx.IssueResolutionPhotos
            .AsNoTracking().Where(p => p.IssueId == issue.Id)
            .OrderBy(p => p.DisplayOrder).ToListAsync();

        stored.Select(p => p.Url).Should()
            .Equal("https://cdn.test/after-1.jpg", "https://cdn.test/after-2.jpg");
        stored.Select(p => p.DisplayOrder).Should().Equal(0, 1);

        // The stamp the clients date the resolved banner from. UpdatedAt cannot stand in for it:
        // any later edit or vote moves that one.
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolving_Without_Proof_Should_Still_Resolve()
    {
        // Attaching proof is optional, and the bare resolve stays the common case — the
        // my-issues card offers no picker at all and sends exactly this request.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Resolved },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Resolved);
        (await ctx.IssueResolutionPhotos.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reopening_Should_Delete_The_Proof_And_Clear_ResolvedAt()
    {
        // The invariant every client renders on: a resolution photo set exists only while the
        // issue is Resolved. Left behind, the proof would strand on an Active issue, and could
        // resurface later on a resolve that attached nothing of its own.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls = ["https://cdn.test/after.jpg"]
            },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.IssueResolutionPhotos.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task Re_Resolving_Should_Replace_The_Previous_Proof_Rather_Than_Accumulate()
    {
        // Replace-set semantics: what is displayed is what the LATEST resolve attached.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls = ["https://cdn.test/first.jpg"]
            },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest { Status = IssueStatus.Active },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls = ["https://cdn.test/second.jpg"]
            },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.IssueResolutionPhotos.AsNoTracking().ToListAsync())
            .Select(p => p.Url).Should().Equal("https://cdn.test/second.jpg");
    }

    [Theory]
    [InlineData(IssueStatus.Cancelled)]
    [InlineData(IssueStatus.Active)]
    public async Task Proof_Should_Be_Rejected_On_Every_Transition_But_Resolving(
        IssueStatus nonResolvingTarget)
    {
        // Rejected rather than dropped: a client attaching proof to a cancel has misunderstood
        // the call, and silently succeeding would hide that until someone noticed the photos
        // were never displayed anywhere.
        IssueStatus seedStatus = nonResolvingTarget == IssueStatus.Active
            ? IssueStatus.Resolved
            : IssueStatus.Active;
        var (owner, issue) = await SeedAsync(seedStatus);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = nonResolvingTarget,
                ResolutionPhotoUrls = ["https://cdn.test/after.jpg"]
            },
            owner.SupabaseUserId);

        success.Should().BeFalse();
        error.Should().Be(DomainErrors.ResolutionPhotosRequireResolved);

        // Nothing moved — the guard runs before the transaction opens.
        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(seedStatus);
    }

    [Fact]
    public async Task Too_Much_Proof_Should_Be_Rejected_And_Leave_The_Issue_Untouched()
    {
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        var (success, error) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls = Enumerable
                    .Range(0, IssueValidationLimits.MaxResolutionPhotoCount + 1)
                    .Select(i => $"https://cdn.test/after-{i}.jpg")
                    .ToList()
            },
            owner.SupabaseUserId);

        success.Should().BeFalse();
        error.Should().Contain(IssueValidationLimits.MaxResolutionPhotoCount.ToString());

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Active);
        (await ctx.IssueResolutionPhotos.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    public async Task Proof_Urls_Should_Meet_The_Same_Scheme_Guard_As_Issue_Photos(string hostileUrl)
    {
        // These are echoed back in IssueDetailResponse.ResolutionPhotos and rendered by the same
        // clients, so a javascript: or data: URI is exactly as dangerous here as on an issue
        // photo. The guard is shared with IssuePhotoWriter; pinned separately so a later
        // refactor cannot quietly drop it from this path.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        var (success, _) = await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls = [hostileUrl]
            },
            owner.SupabaseUserId);

        success.Should().BeFalse();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id))
            .Status.Should().Be(IssueStatus.Active);
    }

    [Fact]
    public async Task Blank_Proof_Slots_Should_Not_Count_Against_The_Cap()
    {
        // A client picker with fixed-size slots sends trailing blanks. They are dropped rather
        // than counted, so a genuine three-photo set is not rejected as a fourth.
        var (owner, issue) = await SeedAsync(IssueStatus.Active);

        (await CreateService().UpdateIssueStatusAsync(
            issue.Id,
            new UpdateIssueStatusRequest
            {
                Status = IssueStatus.Resolved,
                ResolutionPhotoUrls =
                [
                    "https://cdn.test/a.jpg", "  ", "https://cdn.test/b.jpg",
                    "", "https://cdn.test/c.jpg"
                ]
            },
            owner.SupabaseUserId)).Success.Should().BeTrue();

        using var ctx = _dbFactory.CreateContext();
        (await ctx.IssueResolutionPhotos.AsNoTracking().OrderBy(p => p.DisplayOrder).ToListAsync())
            .Select(p => p.Url).Should()
            .Equal("https://cdn.test/a.jpg", "https://cdn.test/b.jpg", "https://cdn.test/c.jpg");
    }

    /// <summary>
    /// Slips a competing status change in immediately before the service's own transition claim,
    /// so the claim finds the row already moved and matches nothing.
    /// </summary>
    private sealed class RivalTransitionInterceptor(string rivalSql) : DbCommandInterceptor
    {
        public int? RowsStolen { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (RowsStolen is null && command.CommandText.Contains("UPDATE \"Issues\"")
                && command.CommandText.Contains("\"Status\""))
            {
                RowsStolen = 0;
                using DbCommand rival = command.Connection!.CreateCommand();
                rival.Transaction = command.Transaction;
                rival.CommandText = rivalSql;
                RowsStolen = rival.ExecuteNonQuery();
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
