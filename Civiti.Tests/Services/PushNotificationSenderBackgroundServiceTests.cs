using System.Net;
using System.Threading.Channels;
using Civiti.Api.Data;
using Civiti.Api.Infrastructure.Configuration;
using Civiti.Api.Models.Domain;
using Civiti.Api.Models.Push;
using Civiti.Api.Services;
using Civiti.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Civiti.Tests.Services;

public class PushNotificationSenderBackgroundServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<ILogger<PushNotificationSenderBackgroundService>> _logger = new();
    private readonly ExpoPushConfiguration _config = new() { BatchSize = 100 };

    public void Dispose() => _dbFactory.Dispose();

    private IServiceScopeFactory CreateScopeFactory()
    {
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(() =>
        {
            var scope = new Mock<IServiceScope>();
            var sp = new Mock<IServiceProvider>();
            sp.Setup(s => s.GetService(typeof(CivitiDbContext)))
                .Returns(_dbFactory.CreateContext());
            scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
            return scope.Object;
        });
        return factory.Object;
    }

    private (Guid userId, string token) SeedUserWithToken(bool pushEnabled = true)
    {
        using var db = _dbFactory.CreateContext();
        var user = TestDataBuilder.CreateUser();
        user.PushNotificationsEnabled = pushEnabled;
        db.UserProfiles.Add(user);

        var pushToken = new PushToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = $"ExponentPushToken[{Guid.NewGuid():N}]",
            Platform = PushTokenPlatform.Ios
        };
        db.PushTokens.Add(pushToken);
        db.SaveChanges();
        return (user.Id, pushToken.Token);
    }

    /// <summary>
    /// Starts the service, writes a single message, and waits deterministically.
    /// For tests expecting HTTP calls, waits for the handler to be invoked
    /// <paramref name="expectedCalls"/> times. Otherwise waits for channel drain.
    /// </summary>
    private async Task<(int callCount, PushNotificationSenderBackgroundService service)> StartServiceWithMessageAsync(
        PushNotificationMessage message, TestHttpHandler handler, int expectedCalls = 0)
    {
        var channel = Channel.CreateUnbounded<PushNotificationMessage>();
        await channel.Writer.WriteAsync(message);
        channel.Writer.Complete();

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient("ExpoPush"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var service = new PushNotificationSenderBackgroundService(
            channel.Reader, CreateScopeFactory(), httpFactory.Object, _config, _logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);

        if (expectedCalls > 0)
            await handler.WaitForCallsAsync(expectedCalls, TimeSpan.FromSeconds(3));
        else
            await channel.Reader.Completion;

        return (handler.CallCount, service);
    }

    [Fact]
    public async Task Should_Skip_When_PushNotifications_Disabled()
    {
        var (userId, _) = SeedUserWithToken(pushEnabled: false);
        var handler = TestHttpHandler.AlwaysReturn(OkExpoResponse());

        var (calls, service) = await StartServiceWithMessageAsync(
            new PushNotificationMessage(userId, "Title", "Body"), handler);
        await service.StopAsync(CancellationToken.None);

        calls.Should().Be(0);
    }

    [Fact]
    public async Task ForceSend_Should_Bypass_Preference_Check()
    {
        var (userId, _) = SeedUserWithToken(pushEnabled: false);
        var handler = TestHttpHandler.AlwaysReturn(OkExpoResponse());

        var (calls, service) = await StartServiceWithMessageAsync(
            new PushNotificationMessage(userId, "Title", "Body", ForceSend: true), handler,
            expectedCalls: 1);
        await service.StopAsync(CancellationToken.None);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Should_Remove_DeviceNotRegistered_Token()
    {
        var (userId, tokenValue) = SeedUserWithToken();
        var handler = TestHttpHandler.AlwaysReturn(DeviceNotRegisteredExpoResponse());

        var (_, service) = await StartServiceWithMessageAsync(
            new PushNotificationMessage(userId, "Title", "Body"), handler,
            expectedCalls: 1);

        // Token removal happens after HTTP response parsing — poll until DB reflects it
        bool tokenRemoved = await PollConditionAsync(() =>
        {
            using var db = _dbFactory.CreateContext();
            return !db.PushTokens.Any(pt => pt.Token == tokenValue);
        }, TimeSpan.FromSeconds(3));

        await service.StopAsync(CancellationToken.None);
        tokenRemoved.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Retry_Once_On_Http_Error_Then_Succeed()
    {
        var (userId, _) = SeedUserWithToken();
        int attempt = 0;
        var handler = new TestHttpHandler(_ =>
            Interlocked.Increment(ref attempt) == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    { Content = new StringContent("server error") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(OkExpoResponse()) });

        var (calls, service) = await StartServiceWithMessageAsync(
            new PushNotificationMessage(userId, "Title", "Body"), handler,
            expectedCalls: 2);
        await service.StopAsync(CancellationToken.None);

        calls.Should().Be(2);
    }

    private static async Task<bool> PollConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return false;
    }

    private static string OkExpoResponse() =>
        """{"data":[{"status":"ok"}]}""";

    private static string DeviceNotRegisteredExpoResponse() =>
        """{"data":[{"status":"error","details":{"error":"DeviceNotRegistered"}}]}""";

    private class TestHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        private int _callCount;
        private TaskCompletionSource? _tcs;
        private int _targetCalls;

        public int CallCount => Volatile.Read(ref _callCount);

        public static TestHttpHandler AlwaysReturn(string jsonBody) =>
            new(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(jsonBody) });

        public Task WaitForCallsAsync(int count, TimeSpan timeout)
        {
            _targetCalls = count;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (Volatile.Read(ref _callCount) >= count)
                _tcs.TrySetResult();

            return _tcs.Task.WaitAsync(timeout);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = factory(request);
            if (Interlocked.Increment(ref _callCount) >= _targetCalls)
                _tcs?.TrySetResult();
            return Task.FromResult(result);
        }
    }
}
