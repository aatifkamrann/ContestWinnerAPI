-- A setup's last test, rewritten over the row it has; no row rewritten
-- means the caller inserts one.
CREATE OR ALTER PROCEDURE [dbo].[SetupTest_Record]
    @kind nvarchar(16),
    @setupId nvarchar(16),
    @ok bit,
    @detail nvarchar(240),
    @fingerprint nvarchar(512),
    @testedAt datetimeoffset,
    @testedBy nvarchar(320) = NULL
AS
BEGIN
    UPDATE [SetupTests]
    SET [Ok] = @ok, [Detail] = @detail, [Fingerprint] = @fingerprint, [TestedAtUtc] = @testedAt, [TestedBy] = @testedBy
    WHERE [Kind] = @kind AND [SetupId] = @setupId;
END
