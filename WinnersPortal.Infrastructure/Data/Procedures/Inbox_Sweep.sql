-- The retention sweep: inbox lines older than the cut-off go, read or not.
CREATE OR ALTER PROCEDURE [dbo].[Inbox_Sweep]
    @cutOff datetimeoffset
AS
BEGIN
    DELETE FROM [Notifications] WHERE [CreatedAtUtc] < @cutOff;
END
