using Microsoft.EntityFrameworkCore;

namespace WinnersPortal.Infrastructure.Data;

/// <summary>
/// Maintains the denormalised feed counters. Every method is one atomic
/// UPDATE whose new value is a subquery over the source of truth — recompute,
/// never increment — so concurrent writes cannot drift a counter and a write
/// missed by a crash heals on the very next one. Called after the unit of
/// work that changed the truth commits, and before any live-board nudge, so
/// refetching tabs read a counter that is already right.
///
/// Raw SQL, deliberately: EF 8 cannot translate navigation counts inside an
/// ExecuteUpdate setter, and splitting into read-then-write would reopen the
/// race the single statement exists to close. The FeedCounters migration's
/// backfill runs these same shapes over every row. On SQL Server the
/// statement is a stored procedure (<c>Recount_Opportunity</c>,
/// <c>Recount_User</c>), called through Dapper as the day-to-day writes
/// are, or through EF Core when Dapper is off for a diagnostic run; on
/// Postgres it is the statement EF Core parameterises from the
/// interpolated string — the id is a parameter either way.
/// </summary>
public static class Recount
{
    /// <summary>The card's own numbers: active entrants and milestones.
    /// Call sites: entry create, entry withdraw, the demo seed.</summary>
    public static Task OpportunityAsync(AppDbContext db, Guid opportunityId, CancellationToken ct) =>
        db.UseDapper
            ? db.Sql.ExecuteAsync(Procedures.RecountOpportunity, new { opportunityId }, ct)
            : db.IsSqlServer
                ? db.Database.ExecuteSqlRawAsync("EXEC " + Procedures.RecountOpportunity.QualifiedName + " @opportunityId = {0}", [opportunityId], ct)
                : db.Database.ExecuteSqlAsync($"""
                    UPDATE "Opportunities" c SET
                        "ActiveEntryCount" = (SELECT count(*) FROM "Entries" e
                            WHERE e."OpportunityId" = c."Id" AND e."Status" = 0),
                        "MilestoneCount" = (SELECT count(*) FROM "Milestones" m
                            WHERE m."OpportunityId" = c."Id")
                    WHERE c."Id" = {opportunityId}
                    """, ct);

    /// <summary>The card's trust line: awards paid as a client, ratings
    /// received in either role. Call sites: payment confirmed (the client),
    /// rating upsert (whoever was rated).</summary>
    public static Task UserAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.UseDapper
            ? db.Sql.ExecuteAsync(Procedures.RecountUser, new { userId }, ct)
            : db.IsSqlServer
                ? db.Database.ExecuteSqlRawAsync("EXEC " + Procedures.RecountUser.QualifiedName + " @userId = {0}", [userId], ct)
                : db.Database.ExecuteSqlAsync($"""
                    UPDATE "Users" u SET
                        "AwardsPaidCount" = (SELECT count(*) FROM "Awards" a
                            JOIN "Opportunities" c ON c."Id" = a."OpportunityId"
                            WHERE c."ClientId" = u."Id" AND a."PaidAtUtc" IS NOT NULL),
                        "RatingCount" = (SELECT count(*) FROM "Ratings" r WHERE r."OfUserId" = u."Id"),
                        "RatingSum" = (SELECT coalesce(sum(r."Stars"), 0) FROM "Ratings" r WHERE r."OfUserId" = u."Id")
                    WHERE u."Id" = {userId}
                    """, ct);
}
