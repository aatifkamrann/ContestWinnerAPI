-- The welcome page's account facts: name, role, what was confirmed.
CREATE OR ALTER PROCEDURE [dbo].[Welcome_Account]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.[DisplayName], u.[Role], u.[EmailConfirmedAtUtc], u.[PhoneConfirmedAtUtc]
    FROM [Users] u
    WHERE u.[Id] = @userId AND u.[ErasedAtUtc] IS NULL;
END
