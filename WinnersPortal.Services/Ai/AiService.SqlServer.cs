using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The SQL Server half of the AI service's reads: the stored artifact for
/// a feature and subject, and the ownership checks before a request, each
/// a procedure. The queue and the re-read after it stay EF Core: they are
/// writes.
/// </summary>
public sealed partial class AiService
{
    private static Task<AiArtifact?> ArtifactSqlAsync(Sql sql, AiFeature feature, Guid subjectId, CancellationToken ct) =>
        sql.SingleOrDefaultAsync<AiArtifact>(Procedures.AiArtifact, new { feature = (int)feature, subjectId }, ct);

    private sealed record ModeratedRaw(int Status, bool HasEntries);

    private static async Task<ModeratedOpportunity?> ModeratedSqlAsync(Sql sql, Guid id, Guid? viewerId, bool isAdmin, CancellationToken ct)
    {
        var raw = await sql.SingleOrDefaultAsync<ModeratedRaw>(Procedures.AiModerated, new { id, isAdmin, viewerId }, ct);
        return raw is null ? null : new ModeratedOpportunity((OpportunityStatus)raw.Status, raw.HasEntries);
    }

    private static async Task<bool> OwnsOpportunitySqlAsync(Sql sql, Guid id, Guid? clientId, CancellationToken ct) =>
        await sql.ScalarAsync<int>(Procedures.AiOwnsOpportunity, new { id, clientId }, ct) == 1;

    private sealed record EntryGateRaw(int Status, DateTimeOffset? FrozenAtUtc);

    private static async Task<EntryGate?> EntryGateSqlAsync(Sql sql, Guid id, Guid? clientId, CancellationToken ct)
    {
        var raw = await sql.SingleOrDefaultAsync<EntryGateRaw>(Procedures.AiEntryGate, new { id, clientId }, ct);
        return raw is null ? null : new EntryGate((EntryStatus)raw.Status, raw.FrozenAtUtc);
    }

    private static async Task<bool> OwnsEntrySqlAsync(Sql sql, Guid id, Guid? clientId, CancellationToken ct) =>
        await sql.ScalarAsync<int>(Procedures.AiOwnsEntry, new { id, clientId }, ct) == 1;
}
