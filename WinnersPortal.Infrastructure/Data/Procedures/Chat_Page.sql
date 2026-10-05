-- One page of a conversation, newest first, keyset-paged on (CreatedAtUtc,
-- Id) from the last line of the page before, both null for the first page;
-- the caller asks for one more than it shows to learn whether there is
-- another. The caller has already passed Chat_Thread's door for this entry.
CREATE OR ALTER PROCEDURE [dbo].[Chat_Page]
    @entryId uniqueidentifier,
    @take int,
    @beforeAt datetimeoffset = NULL,
    @beforeId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@take) m.[Id], m.[SenderId], m.[Body], m.[CreatedAtUtc], m.[ReadAtUtc]
    FROM [ChatMessages] m
    WHERE m.[EntryId] = @entryId
      AND (@beforeAt IS NULL OR m.[CreatedAtUtc] < @beforeAt OR (m.[CreatedAtUtc] = @beforeAt AND m.[Id] < @beforeId))
    ORDER BY m.[CreatedAtUtc] DESC, m.[Id] DESC
    OPTION (RECOMPILE);
END
