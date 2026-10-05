using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// Why a call may not run now — or <see cref="Allowed"/>, and counted.
/// The order a member's press is judged in is the order of the cheapest
/// refusal: the minute's burst, their day, the portal's day, then the
/// portal's money.
/// </summary>
public enum AiQuotaVerdict
{
    Allowed,
    /// <summary>This member has pressed more times this minute than the setting allows.</summary>
    MemberBurst,
    /// <summary>This member has spent their day's inline calls.</summary>
    MemberDay,
    /// <summary>The portal's daily ceiling is reached.</summary>
    Portal,
    /// <summary>Today's estimated spend, at the saved prices, has reached the daily budget.</summary>
    Budget,
}

/// <summary>
/// Who a call is spent by: the portal alone — a worker job, the settings
/// test — or a member pressing an inline tool, who is also held to the
/// per-member caps unless exempt (an administrator).
/// </summary>
public readonly record struct AiSpender(Guid? MemberId, bool Exempt)
{
    public static AiSpender Portal => new(null, true);

    public static AiSpender Member(Guid id, bool isAdmin) => new(id, isAdmin);

    /// <summary>True when this spender's own counts are consulted, not only the portal's.</summary>
    public bool Capped => MemberId is not null && !Exempt;
}

/// <summary>
/// The daily ceiling, the per-member caps and the daily budget. Every
/// provider call — worker jobs, inline tools and the admin's test button
/// alike — consumes through here, so the count the ceiling compares against
/// is the count that actually left the building. Each count is one row of
/// <see cref="AiUsage"/> per day, taken in a single statement that only
/// succeeds under the limit (a procedure on SQL Server, an ExecuteUpdate
/// on Postgres), so two calls arriving together cannot both read the same
/// number and both pass. The minute's burst is counted in memory: it
/// guards a key held down, not a budget, and a second API process
/// counting its own is near enough. Once the provider has answered, the
/// tokens it counted are added to the same rows and to the day's row for
/// the feature and model (<see cref="AiSpend"/>), which is what the usage
/// panel and the budget read.
/// </summary>
public sealed class AiQuota(IServiceScopeFactory scopes, AiOptions ai)
{
    /// <summary>The portal's own row carries the empty id.</summary>
    public static readonly Guid PortalScope = Guid.Empty;

    private readonly AiBurst burst = new();

    /// <summary>The verdict, and when allowed the call is counted — the caller skips on anything else, never queues.</summary>
    public async Task<AiQuotaVerdict> ConsumeAsync(AiSpender who, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var day = AiQuotaRules.DayKey(now);
        if (who.Capped)
        {
            var limits = await ai.LimitsAsync(ct);
            if (!burst.TryAdmit(who.MemberId!.Value, now, limits.MemberPerMinute)) return AiQuotaVerdict.MemberBurst;
            if (!await ConsumeRowAsync(day, who.MemberId.Value, limits.MemberDailyLimit, ct)) return AiQuotaVerdict.MemberDay;
        }
        if (!await ConsumeRowAsync(day, PortalScope, await ai.DailyCallLimitAsync(ct), ct))
        {
            // The member's unit was taken for a call the portal then refused.
            if (who.Capped) await RefundRowAsync(day, who.MemberId!.Value, ct);
            return AiQuotaVerdict.Portal;
        }
        // The money, last: it is the one check that reads a month's rows
        // and does arithmetic, and most calls never reach a budget.
        if (await BudgetReachedAsync(day, ct))
        {
            await RefundAsync(who, ct);
            return AiQuotaVerdict.Budget;
        }
        return AiQuotaVerdict.Allowed;
    }

    /// <summary>
    /// Uncount a call the provider never ran — a 429, a 5xx, or the pause
    /// after repeated failures cost nothing, and a ceiling that counts those
    /// is a spend limit measuring the wrong thing. A refusal the request
    /// itself earned (a 4xx) is not refunded, nor is an attempt that timed
    /// out: the provider may have run it.
    /// </summary>
    public async Task RefundAsync(AiSpender who, CancellationToken ct)
    {
        var day = AiQuotaRules.DayKey(DateTimeOffset.UtcNow);
        await RefundRowAsync(day, PortalScope, ct);
        if (who.Capped) await RefundRowAsync(day, who.MemberId!.Value, ct);
    }

    /// <summary>
    /// What a call cost once the provider has answered: its tokens, added
    /// to the portal's day row, the member's where there is one, and the
    /// day's row for the feature and model. Called for a failure with a
    /// bill too — an answer cut off at the token budget is the dearest
    /// kind — and never for a call the provider did not run. Nothing here
    /// can fail the call: the answer is already in hand.
    /// </summary>
    public async Task SpendAsync(AiSpender who, string feature, string provider, string model, AiTokens tokens, CancellationToken ct)
    {
        var day = AiQuotaRules.DayKey(DateTimeOffset.UtcNow);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await TokensOnRowAsync(db, day, PortalScope, tokens, ct);
        if (who.Capped) await TokensOnRowAsync(db, day, who.MemberId!.Value, tokens, ct);
        await SpendRowAsync(db, day, feature, provider.ToLowerInvariant(), model, tokens, ct);
    }

    /// <summary>Today's portal count beside the ceiling, for the settings test's line.</summary>
    public async Task<(int Used, int Limit)> StateAsync(CancellationToken ct)
    {
        var day = AiQuotaRules.DayKey(DateTimeOffset.UtcNow);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var used = db.UseDapper
            ? await db.Sql.ScalarAsync<int>(Procedures.AiUsageToday, new { day, userId = PortalScope }, ct)
            : await db.AiUsages.Where(u => u.Day == day && u.UserId == PortalScope).Select(u => u.Calls).SingleOrDefaultAsync(ct);
        return (used, await ai.DailyCallLimitAsync(ct));
    }

    /// <summary>The day rows from a day on, for the usage panel and the budget; a month is a few hundred rows at most.</summary>
    public async Task<List<AiSpend>> SpendSinceAsync(string fromDay, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.UseDapper
            ? await db.Sql.QueryAsync<AiSpend>(Procedures.AiSpendSince, new { fromDay }, ct)
            : await db.AiSpends.AsNoTracking().Where(s => string.Compare(s.Day, fromDay) >= 0).ToListAsync(ct);
    }

    /// <summary>
    /// Whether today's estimate has reached the budget. Only a priced
    /// model counts, so a budget with no prices saved never trips; the
    /// operator is told so beside the field.
    /// </summary>
    private async Task<bool> BudgetReachedAsync(string day, CancellationToken ct)
    {
        if (await ai.DailyBudgetUsdAsync(ct) is not { } budget) return false;
        var prices = await ai.PricesAsync(ct);
        if (!prices.Any) return false;
        return AiUsageReport.CostToday(await SpendSinceAsync(day, ct), day, prices) >= budget;
    }

    private async Task<bool> ConsumeRowAsync(string day, Guid userId, int limit, CancellationToken ct)
    {
        if (limit <= 0) return false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.UseDapper)
            return await db.Sql.ScalarAsync<int>(Procedures.AiConsume, new { day, userId, limit }, ct) == 1;

        // One statement that only counts under the limit; the row is made
        // on the day's first call, and when two first calls race the loser
        // counts on the row the winner made.
        if (await CountOnRowAsync(db, day, userId, limit, ct)) return true;
        if (await db.AiUsages.AnyAsync(u => u.Day == day && u.UserId == userId, ct)) return false;
        db.AiUsages.Add(new AiUsage { Day = day, UserId = userId, Calls = 1 });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await CountOnRowAsync(db, day, userId, limit, ct);
        }
    }

    private static async Task<bool> CountOnRowAsync(AppDbContext db, string day, Guid userId, int limit, CancellationToken ct) =>
        await db.AiUsages
            .Where(u => u.Day == day && u.UserId == userId && u.Calls < limit)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Calls, u => u.Calls + 1), ct) == 1;

    private async Task RefundRowAsync(string day, Guid userId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.UseDapper)
        {
            await db.Sql.ExecuteAsync(Procedures.AiRefund, new { day, userId }, ct);
            return;
        }
        await db.AiUsages
            .Where(u => u.Day == day && u.UserId == userId && u.Calls > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Calls, u => u.Calls - 1), ct);
    }

    /// <summary>Adds the tokens to a day row that the count already made; a row that is not there (the count rolled over at midnight) is made with them.</summary>
    private static async Task TokensOnRowAsync(AppDbContext db, string day, Guid userId, AiTokens tokens, CancellationToken ct)
    {
        if (db.UseDapper)
        {
            await db.Sql.ExecuteAsync(Procedures.AiTokens, new { day, userId, input = tokens.Input, output = tokens.Output }, ct);
            return;
        }
        var added = await db.AiUsages
            .Where(u => u.Day == day && u.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.InputTokens, u => u.InputTokens + tokens.Input)
                .SetProperty(u => u.OutputTokens, u => u.OutputTokens + tokens.Output), ct);
        if (added == 1) return;
        db.AiUsages.Add(new AiUsage { Day = day, UserId = userId, Calls = 0, InputTokens = tokens.Input, OutputTokens = tokens.Output });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await db.AiUsages
                .Where(u => u.Day == day && u.UserId == userId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.InputTokens, u => u.InputTokens + tokens.Input)
                    .SetProperty(u => u.OutputTokens, u => u.OutputTokens + tokens.Output), ct);
        }
    }

    private static async Task SpendRowAsync(AppDbContext db, string day, string feature, string provider, string model, AiTokens tokens, CancellationToken ct)
    {
        if (db.UseDapper)
        {
            await db.Sql.ExecuteAsync(Procedures.AiSpend, new { day, feature, provider, model, input = tokens.Input, output = tokens.Output }, ct);
            return;
        }
        if (await AddToSpendAsync(db, day, feature, provider, model, tokens, ct)) return;
        db.AiSpends.Add(new AiSpend
        {
            Day = day, Feature = feature, Provider = provider, Model = model,
            Calls = 1, InputTokens = tokens.Input, OutputTokens = tokens.Output,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await AddToSpendAsync(db, day, feature, provider, model, tokens, ct);
        }
    }

    private static async Task<bool> AddToSpendAsync(AppDbContext db, string day, string feature, string provider, string model, AiTokens tokens, CancellationToken ct) =>
        await db.AiSpends
            .Where(s => s.Day == day && s.Feature == feature && s.Provider == provider && s.Model == model)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Calls, x => x.Calls + 1)
                .SetProperty(x => x.InputTokens, x => x.InputTokens + tokens.Input)
                .SetProperty(x => x.OutputTokens, x => x.OutputTokens + tokens.Output), ct) == 1;
}

/// <summary>
/// The minute's burst, per member, in memory: the moments of their last
/// presses, trimmed to the window. A refused press is counted too — a key
/// held down keeps being refused, rather than admitted once a minute.
/// </summary>
public sealed class AiBurst
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, Queue<DateTimeOffset>> presses = [];
    private readonly object gate = new();

    /// <summary>True when this press is within the member's allowance for the minute; the press is counted either way.</summary>
    public bool TryAdmit(Guid member, DateTimeOffset now, int perMinute)
    {
        lock (gate)
        {
            if (!presses.TryGetValue(member, out var queue))
                presses[member] = queue = new Queue<DateTimeOffset>();
            while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
            var admitted = queue.Count < perMinute;
            queue.Enqueue(now);
            // The others' queues drain as they are read; a member who never
            // returns would hold a minute's presses for ever, so sweep now and then.
            if (presses.Count > 1000)
                foreach (var (key, q) in presses.ToList())
                    if (q.Count == 0 || now - q.Last() >= Window) presses.Remove(key);
            return admitted;
        }
    }
}
