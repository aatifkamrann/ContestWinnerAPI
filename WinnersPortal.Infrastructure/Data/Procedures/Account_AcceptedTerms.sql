-- The version of the terms the account accepted, null for none yet.
CREATE OR ALTER PROCEDURE [dbo].[Account_AcceptedTerms]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.[AcceptedTermsVersion] FROM [Users] u WHERE u.[Id] = @userId;
END
