using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Admin;

/// <summary>The SQL Server half of the operations console's three reads: <c>Admin_Operations</c>, three result sets in one round trip.</summary>
public sealed partial class AdminService
{
    private sealed record DeliveryRead(
        Guid Id, string Source, string DeliveryId, string Event, string? Action, string? RepoFullName, string? HandledNote,
        DateTimeOffset ReceivedAtUtc);

    private static Task<OperationsReads> OperationsSqlAsync(Sql sql, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.AdminOperations, null, async grid => new OperationsReads(
            (await grid.ReadAsync<ProvisioningRead>()).ToList(),
            (await grid.ReadAsync<HandoverRead>()).ToList(),
            (await grid.ReadAsync<DeliveryRead>()).Select(d => new WebhookDeliveryRow
            {
                Id = d.Id, Source = d.Source, DeliveryId = d.DeliveryId, Event = d.Event, Action = d.Action,
                RepoFullName = d.RepoFullName, HandledNote = d.HandledNote, ReceivedAtUtc = d.ReceivedAtUtc,
            }).ToList()), ct);
}
