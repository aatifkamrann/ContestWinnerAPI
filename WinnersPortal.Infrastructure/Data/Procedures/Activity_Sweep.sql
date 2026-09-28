-- The retention sweep: rows older than the cut-off go.
CREATE OR ALTER PROCEDURE [dbo].[Activity_Sweep]
    @cutOff datetimeoffset
AS
BEGIN
    DELETE FROM [ActivityEvents] WHERE [AtUtc] < @cutOff;
END
