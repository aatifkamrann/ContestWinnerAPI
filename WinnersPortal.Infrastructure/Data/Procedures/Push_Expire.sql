-- Push messages still pending from before the cut-off are marked failed,
-- with the reason; answers how many.
CREATE OR ALTER PROCEDURE [dbo].[Push_Expire]
    @cutoff datetimeoffset,
    @reason nvarchar(400)
AS
BEGIN
    UPDATE [PushMessages] SET [Status] = 2 /* Failed */, [LastError] = @reason
    WHERE [Status] = 0 /* Pending */ AND [CreatedAtUtc] < @cutoff;
END
