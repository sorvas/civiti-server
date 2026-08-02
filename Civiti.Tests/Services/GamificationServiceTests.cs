using Civiti.Domain.Entities;
using Civiti.Infrastructure.Services;
using Civiti.Application.Services;
using Civiti.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Civiti.Tests.Services;

public class GamificationServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<ILogger<GamificationService>> _logger = new();
    private readonly Mock<INotificationService> _notificationService = new();

    private GamificationService CreateService()
    {
        var context = _dbFactory.CreateContext();
        return new GamificationService(_logger.Object, context, _notificationService.Object);
    }

    public void Dispose() => _dbFactory.Dispose();

    // ── AwardPointsAsync ──

    [Fact]
    public async Task AwardPoints_Should_Add_Points_To_User()
    {
        var user = TestDataBuilder.CreateUser(points: 10, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 20, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(30);
    }

    [Fact]
    public async Task AwardPoints_Should_Silently_Return_When_User_Not_Found()
    {
        var svc = CreateService();

        // Should not throw
        await svc.AwardPointsAsync(Guid.NewGuid(), 10, "test");
    }

    [Fact]
    public async Task AwardPoints_Should_Trigger_Level_Up()
    {
        // Level 2 requires 50 points (level formula: (level-1)*50 + previous)
        var user = TestDataBuilder.CreateUser(points: 40, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 15, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(55);
        updated.Level.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task AwardPoints_Should_Queue_LevelUp_Notification()
    {
        var user = TestDataBuilder.CreateUser(points: 40, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 15, "test");

        _notificationService.Verify(
            n => n.NotifyLevelUpAsync(It.IsAny<UserProfile>(), It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task AwardPoints_Should_Award_A_Level_Badge_On_The_Award_That_Reaches_It()
    {
        // Points move in SQL now, so the tracked profile this method loaded is stale the moment
        // the balance changes. The level is derived from that balance and CheckBadgeRequirement
        // judges "level" badges against it, so without the reload the badge is missed on the very
        // award that earns it -- and only lands on some later, unrelated award.
        var user = TestDataBuilder.CreateUser(points: 40, level: 1);
        var badge = TestDataBuilder.CreateBadge(
            name: "Level Two", requirementType: "level", requirementValue: 2);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Badges.Add(badge);
            await ctx.SaveChangesAsync();
        }

        // One service instance, so the award and the badge check share a DbContext exactly as
        // the scoped DI registration makes them share one per request.
        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 15, "test");
        await svc.CheckAndAwardBadgesAsync(user.Id);

        using var verifyCtx = _dbFactory.CreateContext();
        var stored = await verifyCtx.UserProfiles.FindAsync(user.Id);
        // 40 + 15 = 55 crosses into level 2, which earns the badge, whose Common rarity pays
        // a further 50. Asserting the total therefore also pins that the badge bonus landed.
        stored!.Points.Should().Be(105);
        stored.Level.Should().Be(2);
        verifyCtx.UserBadges.Where(ub => ub.UserId == user.Id).ToList()
            .Should().ContainSingle(ub => ub.BadgeId == badge.Id,
                "the level badge is earned by this award, not by the next one");
    }

    [Fact]
    public async Task Awarding_Points_Twice_Should_Accumulate_Across_Calls_On_One_Context()
    {
        // The read-modify-write this replaced kept the total on a tracked entity, so a second
        // award computed from the first one's starting value. True concurrency is not expressible
        // here -- every context shares one SQLite connection -- but sequential awards through one
        // service still pin that each award is applied to the row's real balance.
        var user = TestDataBuilder.CreateUser(points: 0, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 10, "first");
        await svc.AwardPointsAsync(user.Id, 10, "second");
        await svc.DeductPointsAsync(user.Id, 5, "refund");

        using var verifyCtx = _dbFactory.CreateContext();
        (await verifyCtx.UserProfiles.FindAsync(user.Id))!.Points.Should().Be(15);
    }
    // ── DeductPointsAsync ──

    [Fact]
    public async Task DeductPoints_Should_Subtract_Points()
    {
        var user = TestDataBuilder.CreateUser(points: 100, level: 2);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.DeductPointsAsync(user.Id, 30, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(70);
    }

    [Fact]
    public async Task DeductPoints_Should_Floor_At_Zero()
    {
        var user = TestDataBuilder.CreateUser(points: 10, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.DeductPointsAsync(user.Id, 50, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(0);
    }

    [Fact]
    public async Task DeductPoints_Should_Adjust_Level_Down()
    {
        // User at level 2 (50+ points). Deduct enough to drop below level 2 threshold
        var user = TestDataBuilder.CreateUser(points: 55, level: 2);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.DeductPointsAsync(user.Id, 50, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(5);
        updated.Level.Should().Be(1);
    }

    [Fact]
    public async Task DeductPoints_Should_Silently_Return_When_User_Not_Found()
    {
        var svc = CreateService();
        await svc.DeductPointsAsync(Guid.NewGuid(), 10, "test");
    }

    // ── CheckAndAwardBadgesAsync ──

    [Fact]
    public async Task CheckAndAwardBadges_Should_Award_When_Criteria_Met()
    {
        var user = TestDataBuilder.CreateUser(points: 0);
        user.IssuesReported = 5;
        var badge = TestDataBuilder.CreateBadge(
            requirementType: "issues_reported",
            requirementValue: 5,
            rarity: BadgeRarity.Common);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Badges.Add(badge);
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.CheckAndAwardBadgesAsync(user.Id);

        using var verifyCtx = _dbFactory.CreateContext();
        var userBadges = verifyCtx.UserBadges.Where(ub => ub.UserId == user.Id).ToList();
        userBadges.Should().HaveCount(1);
        userBadges[0].BadgeId.Should().Be(badge.Id);
    }

    [Fact]
    public async Task CheckAndAwardBadges_Should_Judge_A_Counter_Moved_In_SQL_Not_A_Stale_Copy()
    {
        // Counters move in SQL now (IssuesReported, IssuesResolved, CommunityVotes, Points ->
        // Level), which writes past the change tracker. Any caller that already had the profile
        // tracked keeps the pre-move copy, and EF identity resolution hands that same instance
        // back to the query this method runs -- so the badge earned by the move would be judged
        // against the number from before it.
        //
        // Today every production path happens to award points first, and AwardPointsAsync reloads,
        // which masks this. That is call-order luck, not a guarantee, so pin it directly: move a
        // counter with no intervening award and require the badge to still land.
        var user = TestDataBuilder.CreateUser();
        var badge = TestDataBuilder.CreateBadge(
            name: "Reporter", requirementType: "issues_reported", requirementValue: 5);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Badges.Add(badge);
            await ctx.SaveChangesAsync();
        }

        using var shared = _dbFactory.CreateContext();

        // Track the profile first, exactly as a caller would before doing its own work.
        UserProfile tracked = await shared.UserProfiles.FirstAsync(u => u.Id == user.Id);
        tracked.IssuesReported.Should().Be(0);

        // Move the counter past the change tracker. `tracked` still reads 0 after this.
        await shared.UserProfiles
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.IssuesReported, 5));
        tracked.IssuesReported.Should().Be(0, "ExecuteUpdate deliberately bypasses the tracker");

        var svc = new GamificationService(_logger.Object, shared, _notificationService.Object);
        await svc.CheckAndAwardBadgesAsync(user.Id);

        using var verifyCtx = _dbFactory.CreateContext();
        verifyCtx.UserBadges.Where(ub => ub.UserId == user.Id).ToList()
            .Should().ContainSingle(ub => ub.BadgeId == badge.Id,
                "the badge must be judged against the row, not against the tracked copy");
    }
    [Fact]
    public async Task CheckAndAwardBadges_Should_Skip_Already_Earned()
    {
        var user = TestDataBuilder.CreateUser();
        user.IssuesReported = 10;
        var badge = TestDataBuilder.CreateBadge(requirementType: "issues_reported", requirementValue: 5);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Badges.Add(badge);
            ctx.UserBadges.Add(new UserBadge
            {
                Id = Guid.NewGuid(), UserId = user.Id, BadgeId = badge.Id, EarnedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.CheckAndAwardBadgesAsync(user.Id);

        using var verifyCtx = _dbFactory.CreateContext();
        verifyCtx.UserBadges.Where(ub => ub.UserId == user.Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task CheckAndAwardBadges_Should_Award_Rarity_Based_Points()
    {
        var user = TestDataBuilder.CreateUser(points: 0);
        user.IssuesReported = 5;
        var badge = TestDataBuilder.CreateBadge(
            requirementType: "issues_reported",
            requirementValue: 5,
            rarity: BadgeRarity.Rare);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Badges.Add(badge);
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.CheckAndAwardBadgesAsync(user.Id);

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(200); // Rare = 200 points
    }

    [Fact]
    public async Task CheckAndAwardBadges_Should_Send_Notification()
    {
        var user = TestDataBuilder.CreateUser();
        user.IssuesReported = 5;
        var badge = TestDataBuilder.CreateBadge(
            name: "Reporter Badge",
            requirementType: "issues_reported",
            requirementValue: 5);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Badges.Add(badge);
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.CheckAndAwardBadgesAsync(user.Id);

        _notificationService.Verify(
            n => n.NotifyBadgeEarnedAsync(It.IsAny<UserProfile>(), "Reporter Badge"),
            Times.Once);
    }

    // ── UpdateAchievementProgressAsync ──

    [Fact]
    public async Task UpdateAchievementProgress_Should_Create_And_Increment()
    {
        var user = TestDataBuilder.CreateUser();
        var achievement = TestDataBuilder.CreateAchievement(achievementType: "issues_reported", maxProgress: 5);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Achievements.Add(achievement);
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.UpdateAchievementProgressAsync(user.Id, "issues_reported");

        using var verifyCtx = _dbFactory.CreateContext();
        var ua = verifyCtx.UserAchievements
            .FirstOrDefault(x => x.UserId == user.Id && x.AchievementId == achievement.Id);
        ua.Should().NotBeNull();
        ua!.Progress.Should().Be(1);
        ua.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAchievementProgress_Absolute_Should_Set_Value_Directly()
    {
        var user = TestDataBuilder.CreateUser();
        var achievement = TestDataBuilder.CreateAchievement(achievementType: "level_up", maxProgress: 10);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Achievements.Add(achievement);
            ctx.UserAchievements.Add(new UserAchievement
            {
                Id = Guid.NewGuid(), UserId = user.Id, AchievementId = achievement.Id, Progress = 3
            });
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.UpdateAchievementProgressAsync(user.Id, "level_up", progress: 7, isAbsolute: true);

        using var verifyCtx = _dbFactory.CreateContext();
        var ua = verifyCtx.UserAchievements
            .First(x => x.UserId == user.Id && x.AchievementId == achievement.Id);
        ua.Progress.Should().Be(7);
    }

    [Fact]
    public async Task UpdateAchievementProgress_Should_Cap_At_MaxProgress()
    {
        var user = TestDataBuilder.CreateUser();
        var achievement = TestDataBuilder.CreateAchievement(achievementType: "test", maxProgress: 3, rewardPoints: 50);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Achievements.Add(achievement);
            ctx.UserAchievements.Add(new UserAchievement
            {
                Id = Guid.NewGuid(), UserId = user.Id, AchievementId = achievement.Id, Progress = 2
            });
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.UpdateAchievementProgressAsync(user.Id, "test", progress: 5);

        using var verifyCtx = _dbFactory.CreateContext();
        var ua = verifyCtx.UserAchievements
            .First(x => x.UserId == user.Id && x.AchievementId == achievement.Id);
        ua.Progress.Should().Be(3);
        ua.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAchievementProgress_Should_Award_Points_On_Completion()
    {
        var user = TestDataBuilder.CreateUser(points: 0);
        var achievement = TestDataBuilder.CreateAchievement(
            achievementType: "test", maxProgress: 1, rewardPoints: 100);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Achievements.Add(achievement);
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.UpdateAchievementProgressAsync(user.Id, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Points.Should().Be(100);
    }

    // ── Level Calculation Edge Cases ──

    [Fact]
    public async Task Level_Should_Be_1_At_Zero_Points()
    {
        var user = TestDataBuilder.CreateUser(points: 0, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 0, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Level.Should().Be(1);
    }

    [Fact]
    public async Task Level_Should_Be_1_At_49_Points()
    {
        var user = TestDataBuilder.CreateUser(points: 0, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 49, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Level.Should().Be(1);
    }

    [Fact]
    public async Task Level_Should_Be_2_At_50_Points()
    {
        var user = TestDataBuilder.CreateUser(points: 0, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 50, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Level.Should().Be(2);
    }

    [Fact]
    public async Task Level_Should_Be_3_At_150_Points()
    {
        // Level 3 requires: (2)*50 + GetPointsForLevel(2) = 100 + 50 = 150
        var user = TestDataBuilder.CreateUser(points: 0, level: 1);
        using (var ctx = _dbFactory.CreateContext()) { ctx.UserProfiles.Add(user); await ctx.SaveChangesAsync(); }

        var svc = CreateService();
        await svc.AwardPointsAsync(user.Id, 150, "test");

        using var verifyCtx = _dbFactory.CreateContext();
        var updated = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updated!.Level.Should().Be(3);
    }

    // ── Regression: mid-enumeration navigation fixup ──

    [Fact]
    public async Task UpdateAchievementProgress_Completion_Triggering_LevelUp_Should_Not_Throw_CollectionModified()
    {
        // Regression for the create-issue 400 ("Collection was modified; enumeration
        // operation may not execute"). Chain: an "issues_reported" achievement completes
        // inside CheckAndAwardAchievementsAsync's foreach over user.UserAchievements; its
        // reward points cross a level threshold, so AwardPointsAsync calls
        // UpdateAchievementProgressAsync("level_up"), which Adds a brand-new level_up
        // UserAchievement. EF relationship fixup appends that entity to the live
        // user.UserAchievements collection being enumerated -> InvalidOperationException.
        // The repro REQUIRES an active level_up achievement AND no pre-existing level_up
        // UserAchievement, so the Add actually happens mid-loop.
        //
        // Two completing achievements are seeded so the assertions verify the full chain
        // runs end-to-end past the mid-loop Add (both complete, both rewards applied) rather
        // than only that no exception escaped; we also assert the mid-loop-created level_up
        // UserAchievement actually persisted.
        var user = TestDataBuilder.CreateUser(points: 0, level: 1);
        var completing1 = TestDataBuilder.CreateAchievement(
            achievementType: "issues_reported", maxProgress: 1, rewardPoints: 100);
        var completing2 = TestDataBuilder.CreateAchievement(
            achievementType: "issues_reported", maxProgress: 1, rewardPoints: 100);
        var levelUp = TestDataBuilder.CreateAchievement(
            achievementType: "level_up", maxProgress: 10, rewardPoints: 0);

        using (var ctx = _dbFactory.CreateContext())
        {
            ctx.UserProfiles.Add(user);
            ctx.Achievements.AddRange(completing1, completing2, levelUp);
            await ctx.SaveChangesAsync();
        }

        var svc = CreateService();

        // Must not throw — completes the whole gamification chain in one call.
        await svc.UpdateAchievementProgressAsync(user.Id, "issues_reported");

        using var verifyCtx = _dbFactory.CreateContext();

        // Both achievements must be completed — proves the loop processed every element
        // across the mid-loop UserAchievements.Add rather than aborting on it.
        var completedIds = verifyCtx.UserAchievements
            .Where(x => x.UserId == user.Id && x.Completed)
            .Select(x => x.AchievementId)
            .ToList();
        completedIds.Should().Contain(new[] { completing1.Id, completing2.Id });

        // The level_up UserAchievement created mid-loop must have persisted end-to-end.
        verifyCtx.UserAchievements
            .Any(x => x.UserId == user.Id && x.AchievementId == levelUp.Id)
            .Should().BeTrue();

        var updatedUser = await verifyCtx.UserProfiles.FindAsync(user.Id);
        updatedUser!.Points.Should().Be(200); // both 100-point rewards applied
        updatedUser.Level.Should().BeGreaterThan(1);
    }
}
