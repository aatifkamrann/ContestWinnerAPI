-- When the account proved its email and its phone, either or both null.
CREATE OR ALTER PROCEDURE [dbo].[Account_Confirmed]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.[EmailConfirmedAtUtc], u.[PhoneConfirmedAtUtc] FROM [Users] u WHERE u.[Id] = @userId;
END
