-- Whether the day's merit snapshot is already written. 1 or 0.
CREATE OR ALTER PROCEDURE [dbo].[Merit_SnapshotRecorded]
    @today date
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CASE WHEN EXISTS (SELECT 1 FROM [MeritSnapshots] s WHERE s.[DayUtc] = @today) THEN 1 ELSE 0 END;
END
