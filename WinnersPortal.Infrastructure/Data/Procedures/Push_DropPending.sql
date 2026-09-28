-- Whatever was queued for a device's previous holder.
CREATE OR ALTER PROCEDURE [dbo].[Push_DropPending]
    @deviceId uniqueidentifier
AS
BEGIN
    DELETE FROM [PushMessages] WHERE [DeviceId] = @deviceId AND [Status] = 0 /* Pending */;
END
