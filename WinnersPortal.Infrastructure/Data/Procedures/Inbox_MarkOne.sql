-- One of a member's inbox lines read (@readAt set: the first reading's
-- time is kept) or unread (@readAt null). Touches nothing that is not
-- theirs, so the caller reads nought for another member's line.
CREATE OR ALTER PROCEDURE [dbo].[Inbox_MarkOne]
    @userId uniqueidentifier,
    @id uniqueidentifier,
    @readAt datetimeoffset = NULL
AS
BEGIN
    UPDATE [Notifications]
    SET [ReadAtUtc] = CASE WHEN @readAt IS NULL THEN NULL ELSE COALESCE([ReadAtUtc], @readAt) END
    WHERE [Id] = @id AND [UserId] = @userId;
END
