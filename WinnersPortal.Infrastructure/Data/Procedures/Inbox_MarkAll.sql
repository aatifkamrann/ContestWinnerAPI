-- Every one of a member's inbox lines read (@readAt set: only the unread
-- ones change, and keep that time) or unread (@readAt null: only the read
-- ones change). The caller reads how many changed.
CREATE OR ALTER PROCEDURE [dbo].[Inbox_MarkAll]
    @userId uniqueidentifier,
    @readAt datetimeoffset = NULL
AS
BEGIN
    UPDATE [Notifications]
    SET [ReadAtUtc] = @readAt
    WHERE [UserId] = @userId
      AND ((@readAt IS NOT NULL AND [ReadAtUtc] IS NULL) OR (@readAt IS NULL AND [ReadAtUtc] IS NOT NULL));
END
