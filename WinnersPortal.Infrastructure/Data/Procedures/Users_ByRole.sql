-- The ids of every account in a role that is not erased.
CREATE OR ALTER PROCEDURE [dbo].[Users_ByRole]
    @role nvarchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.[Id] FROM [Users] u
    WHERE u.[Role] = @role AND u.[ErasedAtUtc] IS NULL;
END
