-- The other side's lines in a conversation read, as of @readAt: only the
-- unread ones change, and a member's own lines are never touched — their
-- read stamp is the other side's to set. The caller reads how many changed,
-- and has already passed Chat_Thread's door for this entry.
CREATE OR ALTER PROCEDURE [dbo].[Chat_MarkRead]
    @entryId uniqueidentifier,
    @userId uniqueidentifier,
    @readAt datetimeoffset
AS
BEGIN
    UPDATE [ChatMessages]
    SET [ReadAtUtc] = @readAt
    WHERE [EntryId] = @entryId AND [SenderId] <> @userId AND [ReadAtUtc] IS NULL;
END
