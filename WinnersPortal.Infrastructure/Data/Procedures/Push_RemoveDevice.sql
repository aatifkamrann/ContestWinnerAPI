-- A device of this person's; its queued rows go with it (cascade). Nothing
-- removed means it was not theirs, or not there.
CREATE OR ALTER PROCEDURE [dbo].[Push_RemoveDevice]
    @id uniqueidentifier,
    @userId uniqueidentifier
AS
BEGIN
    DELETE FROM [PushDevices] WHERE [Id] = @id AND [UserId] = @userId;
END
