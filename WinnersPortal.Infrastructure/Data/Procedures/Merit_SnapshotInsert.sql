-- The day's batch of merit snapshots, out of its JSON, in one statement.
CREATE OR ALTER PROCEDURE [dbo].[Merit_SnapshotInsert]
    @rows nvarchar(max)
AS
BEGIN
    INSERT INTO [MeritSnapshots] ([UserId], [DayUtc], [Score])
    SELECT [UserId], [DayUtc], [Score]
    FROM OPENJSON(@rows) WITH ([UserId] uniqueidentifier '$.UserId', [DayUtc] date '$.DayUtc', [Score] int '$.Score');
END
