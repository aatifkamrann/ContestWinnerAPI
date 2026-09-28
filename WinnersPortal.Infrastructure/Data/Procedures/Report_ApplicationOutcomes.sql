-- What became of the selected entries, and which won — two result sets
-- over the entry ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Report_ApplicationOutcomes]
    @ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @entries TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @entries SELECT [Id] FROM OPENJSON(@ids) WITH ([Id] uniqueidentifier '$');

    SELECT e.[Id], e.[Status], e.[CreatedAtUtc], e.[PushCount], e.[LastPushAtUtc], e.[WithdrawnReason], e.[RemovedReason],
           (SELECT COUNT(*) FROM [Checkpoints] cp WHERE cp.[EntryId] = e.[Id]) AS [Claimed]
    FROM [Entries] e
    JOIN @entries ids ON ids.[Id] = e.[Id];

    SELECT a.[EntryId], a.[PaidAtUtc]
    FROM [Awards] a
    JOIN @entries ids ON ids.[Id] = a.[EntryId];
END
