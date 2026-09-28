-- A batch of activity rows, read out of their JSON in one INSERT; the id is
-- the table's to give. A third-party call carries its service, its time and
-- the two masked texts; every other row leaves them null.
CREATE OR ALTER PROCEDURE [dbo].[Activity_Insert]
    @rows nvarchar(max)
AS
BEGIN
    INSERT INTO [ActivityEvents]
        ([UserId], [Visitor], [Kind], [Method], [Path], [Action], [Page], [Subject], [Detail], [Status], [Ip], [UserAgent], [AtUtc],
         [Service], [DurationMs], [Request], [Response])
    SELECT [UserId], [Visitor], [Kind], [Method], [Path], [Action], [Page], [Subject], [Detail], [Status], [Ip], [UserAgent], [AtUtc],
           [Service], [DurationMs], [Request], [Response]
    FROM OPENJSON(@rows) WITH (
        [UserId] uniqueidentifier '$.UserId',
        [Visitor] nvarchar(16) '$.Visitor',
        [Kind] nvarchar(8) '$.Kind',
        [Method] nvarchar(8) '$.Method',
        [Path] nvarchar(300) '$.Path',
        [Action] nvarchar(160) '$.Action',
        [Page] nvarchar(300) '$.Page',
        [Subject] nvarchar(200) '$.Subject',
        [Detail] nvarchar(500) '$.Detail',
        [Status] int '$.Status',
        [Ip] nvarchar(45) '$.Ip',
        [UserAgent] nvarchar(300) '$.UserAgent',
        [AtUtc] datetimeoffset '$.AtUtc',
        [Service] nvarchar(16) '$.Service',
        [DurationMs] int '$.DurationMs',
        [Request] nvarchar(max) '$.Request',
        [Response] nvarchar(max) '$.Response');
END
