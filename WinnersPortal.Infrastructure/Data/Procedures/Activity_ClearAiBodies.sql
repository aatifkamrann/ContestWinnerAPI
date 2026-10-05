-- The AI body sweep: an AI call older than the cut-off keeps its row -- who
-- asked, when, the provider and model, the status, the time taken and the
-- token count in its detail -- and loses what was sent and what came back.
CREATE OR ALTER PROCEDURE [dbo].[Activity_ClearAiBodies]
    @cutOff datetimeoffset
AS
BEGIN
    UPDATE [ActivityEvents]
    SET [Request] = NULL, [Response] = NULL
    WHERE [Service] = N'ai' AND [AtUtc] < @cutOff
      AND ([Request] IS NOT NULL OR [Response] IS NOT NULL);
END
