-- The review's account facts: GitHub, and what was confirmed.
CREATE OR ALTER PROCEDURE [dbo].[ProfileReview_Account]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.[GithubLogin], u.[EmailConfirmedAtUtc], u.[PhoneConfirmedAtUtc]
    FROM [Users] u
    WHERE u.[Id] = @userId AND u.[ErasedAtUtc] IS NULL;
END
