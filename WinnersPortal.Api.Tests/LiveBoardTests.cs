using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WinnersPortal.Api.Live;
using WinnersPortal.Services.Live;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The live board's bell (Live/LiveBoard.cs): it rings the opportunity's room
/// with the one event, and it never holds up or fails the write that rang
/// it — which matters once the room lives in Redis, where a broadcast to
/// a Redis that is down waits out its timeout.
/// </summary>
public class LiveBoardTests
{
    private sealed class Proxy(Func<string, object?[], Task> send) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            send(method, args);
    }

    private sealed class Room(IClientProxy proxy, List<string> groups) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) { groups.Add(groupName); return proxy; }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class Hub(IHubClients clients) : IHubContext<OpportunityHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class Capture : ILogger<LiveBoard>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static (LiveBoard Board, List<string> Groups, Capture Log) Make(Func<string, object?[], Task> send)
    {
        var groups = new List<string>();
        var log = new Capture();
        return (new LiveBoard(new Hub(new Room(new Proxy(send), groups)), log), groups, log);
    }

    [Fact]
    public async Task A_nudge_rings_the_opportunities_room_with_the_one_event()
    {
        var sent = new TaskCompletionSource<(string Method, object?[] Args)>();
        var (board, groups, _) = Make((m, a) => { sent.SetResult((m, a)); return Task.CompletedTask; });

        await board.OpportunityChangedAsync("Build-A-Parser");

        var (method, args) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LiveRules.OpportunityGroup("build-a-parser"), Assert.Single(groups));
        Assert.Equal(LiveRules.OpportunityChanged, method);
        Assert.Equal("Build-A-Parser", Assert.Single(args));
    }

    [Fact]
    public async Task A_broadcast_that_never_finishes_does_not_hold_up_the_write_that_rang()
    {
        // A Redis that is down: the publish sits in the backlog until its timeout.
        var never = new TaskCompletionSource();
        var (board, _, _) = Make((_, _) => never.Task);

        var ring = board.OpportunityChangedAsync("slow");

        Assert.True(ring.IsCompletedSuccessfully);
        await ring;
    }

    [Fact]
    public async Task A_broadcast_that_fails_is_a_warning_and_not_the_callers_failure()
    {
        var failed = new TaskCompletionSource();
        var (board, _, log) = Make((_, _) =>
        {
            failed.SetResult();
            return Task.FromException(new InvalidOperationException("No connection is available"));
        });

        await board.OpportunityChangedAsync("down");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The warning is written after the failed send is observed; give the
        // continuation its turn rather than racing it.
        for (var i = 0; i < 50 && log.Lines.Count == 0; i++) await Task.Delay(10);

        var (level, text) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("'down'", text);
    }
}
