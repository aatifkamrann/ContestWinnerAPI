-- Reservations on an opportunity that never became uploads and are past the
-- upload link's life.
CREATE OR ALTER PROCEDURE [dbo].[Attachment_DropStale]
    @id uniqueidentifier,
    @stale datetimeoffset
AS
BEGIN
    DELETE FROM [Attachments]
    WHERE [OpportunityId] = @id AND [UploadedAtUtc] IS NULL AND [CreatedAtUtc] < @stale;
END
