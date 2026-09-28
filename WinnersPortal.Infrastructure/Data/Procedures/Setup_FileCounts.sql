-- The three kinds of row that name a file store, counted per store —
-- three result sets.
CREATE OR ALTER PROCEDURE [dbo].[Setup_FileCounts]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT a.[StorageSetup] AS [Setup], COUNT(*) AS [Count] FROM [Attachments] a GROUP BY a.[StorageSetup];
    SELECT s.[StorageSetup] AS [Setup], COUNT(*) AS [Count] FROM [Submissions] s GROUP BY s.[StorageSetup];
    SELECT e.[ZipStorageSetup] AS [Setup], COUNT(*) AS [Count] FROM [Entries] e
    WHERE e.[ZipStorageKey] IS NOT NULL GROUP BY e.[ZipStorageSetup];
END
