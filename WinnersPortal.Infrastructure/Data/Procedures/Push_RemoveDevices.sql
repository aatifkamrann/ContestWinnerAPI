-- Devices whose subscriptions are gone, the ids as JSON; their queued rows
-- go with them (cascade).
CREATE OR ALTER PROCEDURE [dbo].[Push_RemoveDevices]
    @ids nvarchar(max)
AS
BEGIN
    DELETE d FROM [PushDevices] d
    JOIN OPENJSON(@ids) WITH ([Id] uniqueidentifier '$') ids ON ids.[Id] = d.[Id];
END
