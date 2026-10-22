-- How much each kind of work is used, for Admin -> Categories: a kind in
-- use may be hidden but not removed. Four result sets: opportunities by
-- category and subcategory (any status, drafts included), profiles by
-- primary category, profiles by each secondary category, and past
-- projects by category. Deleted profiles and their projects are left out.
CREATE OR ALTER PROCEDURE [dbo].[Category_Usage]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [Category], [Subcategory], COUNT(*) AS [Count]
    FROM [Opportunities]
    WHERE [Category] IS NOT NULL
    GROUP BY [Category], [Subcategory];

    SELECT [PrimaryCategory] AS [Category], COUNT(*) AS [Count]
    FROM [Profiles]
    WHERE [IsDeleted] = 0 AND [PrimaryCategory] IS NOT NULL
    GROUP BY [PrimaryCategory];

    SELECT s.[Category], COUNT(*) AS [Count]
    FROM [Profiles] p
    CROSS APPLY OPENJSON(p.[SecondaryCategories]) WITH ([Category] nvarchar(40) '$') s
    WHERE p.[IsDeleted] = 0
    GROUP BY s.[Category];

    SELECT pp.[Category], COUNT(*) AS [Count]
    FROM [ProfileProjects] pp
    JOIN [Profiles] p ON p.[UserId] = pp.[UserId]
    WHERE p.[IsDeleted] = 0 AND pp.[Category] IS NOT NULL
    GROUP BY pp.[Category];
END
