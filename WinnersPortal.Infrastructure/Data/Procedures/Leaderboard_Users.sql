-- The board's members: accounts in the role that confirmed an email or a
-- phone and are neither locked nor erased.
CREATE OR ALTER PROCEDURE [dbo].[Leaderboard_Users]
    @role nvarchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.[Id], u.[DisplayName], u.[AvatarUpdatedAtUtc], u.[CreatedAtUtc]
    FROM [Users] u
    WHERE u.[Role] = @role AND u.[ErasedAtUtc] IS NULL AND u.[LockedAtUtc] IS NULL
      AND (u.[EmailConfirmedAtUtc] IS NOT NULL OR u.[PhoneConfirmedAtUtc] IS NOT NULL);
END
