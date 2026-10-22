-- The whole list of kinds of work, in two result sets: the categories,
-- then every subcategory, each in the order the pickers offer them. Read
-- at start and after an administrator saves the list, never per request.
CREATE OR ALTER PROCEDURE [dbo].[Category_List]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [Key], [Label], [SortOrder], [Hidden], [Skills]
    FROM [Categories]
    ORDER BY [SortOrder], [Key];

    SELECT [CategoryKey], [Key], [Label], [SortOrder], [Hidden], [Skills]
    FROM [Subcategories]
    ORDER BY [CategoryKey], [SortOrder], [Key];
END
