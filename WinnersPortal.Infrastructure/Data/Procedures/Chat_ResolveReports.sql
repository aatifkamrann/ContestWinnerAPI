-- A conversation's open reports marked reviewed, all at once, as of @at, by
-- @by, with the note they left (null for none). Reports already reviewed
-- keep their own stamp and note. The caller reads how many changed.
CREATE OR ALTER PROCEDURE [dbo].[Chat_ResolveReports]
    @entryId uniqueidentifier,
    @by uniqueidentifier,
    @at datetimeoffset,
    @note nvarchar(500) = NULL
AS
BEGIN
    UPDATE [ChatReports]
    SET [ResolvedAtUtc] = @at, [ResolvedById] = @by, [Resolution] = @note
    WHERE [EntryId] = @entryId AND [ResolvedAtUtc] IS NULL;
END
