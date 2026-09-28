-- The whole account row, into the entity: every column the table has is a
-- property of the same name, so a column added later rides along the way
-- it does through EF Core.
CREATE OR ALTER PROCEDURE [dbo].[Account_Session]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.* FROM [Users] u WHERE u.[Id] = @userId;
END
