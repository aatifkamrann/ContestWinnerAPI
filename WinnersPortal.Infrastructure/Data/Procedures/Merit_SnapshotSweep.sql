-- Snapshots older than the day given go; the trend window never reaches them.
CREATE OR ALTER PROCEDURE [dbo].[Merit_SnapshotSweep]
    @cutoff date
AS
BEGIN
    DELETE FROM [MeritSnapshots] WHERE [DayUtc] < @cutoff;
END
