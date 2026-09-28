-- The administrator's activity log: how many rows the filter admits, and
-- one page of them newest first — two result sets. Every part of the
-- filter is a parameter, null when it is not applied: one account, one
-- browser session, one kind (visit, action or external), one outside
-- service (ai, identity, github, sms, captcha, push, storage), members
-- only (@members = 1) or visitors only (0), a window, and the typed words
-- as a LIKE pattern matched against every text column but the two
-- third-party texts, which the default collation reads case aside; and,
-- for AI calls, one provider (@aiProvider, a LIKE pattern: its settings
-- key and a slash) or one exact provider/model (@aiModel), both read off
-- the row's Subject. Recompiled per call, so a null filter costs nothing
-- in the plan.
CREATE OR ALTER PROCEDURE [dbo].[Activity_Page]
    @skip int,
    @take int,
    @userId uniqueidentifier = NULL,
    @visitor nvarchar(16) = NULL,
    @kind nvarchar(8) = NULL,
    @service nvarchar(16) = NULL,
    @members bit = NULL,
    @since datetimeoffset = NULL,
    @until datetimeoffset = NULL,
    @q nvarchar(4000) = NULL,
    @aiProvider nvarchar(20) = NULL,
    @aiModel nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(*)
    FROM [ActivityEvents] e
    LEFT JOIN [Users] u ON u.[Id] = e.[UserId]
    WHERE (@userId IS NULL OR e.[UserId] = @userId)
      AND (@visitor IS NULL OR e.[Visitor] = @visitor)
      AND (@kind IS NULL OR e.[Kind] = @kind)
      AND (@service IS NULL OR e.[Service] = @service)
      AND (@aiProvider IS NULL OR (e.[Service] = N'ai' AND e.[Subject] LIKE @aiProvider))
      AND (@aiModel IS NULL OR (e.[Service] = N'ai' AND e.[Subject] = @aiModel))
      AND (@members IS NULL OR (@members = 1 AND e.[UserId] IS NOT NULL) OR (@members = 0 AND e.[UserId] IS NULL))
      AND (@since IS NULL OR e.[AtUtc] >= @since)
      AND (@until IS NULL OR e.[AtUtc] < @until)
      AND (@q IS NULL OR
           (e.[Action] LIKE @q ESCAPE '\' OR e.[Path] LIKE @q ESCAPE '\' OR e.[Page] LIKE @q ESCAPE '\'
            OR e.[Subject] LIKE @q ESCAPE '\' OR e.[Detail] LIKE @q ESCAPE '\' OR e.[Ip] LIKE @q ESCAPE '\'
            OR e.[Visitor] LIKE @q ESCAPE '\' OR u.[DisplayName] LIKE @q ESCAPE '\' OR u.[Email] LIKE @q ESCAPE '\'))
    OPTION (RECOMPILE);

    SELECT e.[Id], e.[AtUtc], e.[Kind], e.[Action], e.[Method], e.[Path], e.[Page], e.[Subject], e.[Detail], e.[Status],
           e.[Ip], e.[UserAgent], e.[Visitor], e.[UserId],
           u.[DisplayName] AS [UserName], u.[Email] AS [UserEmail], u.[Role] AS [UserRole],
           u.[ErasedAtUtc] AS [UserErasedAtUtc], u.[AvatarUpdatedAtUtc] AS [UserAvatarUpdatedAtUtc],
           e.[Service], e.[DurationMs]
    FROM [ActivityEvents] e
    LEFT JOIN [Users] u ON u.[Id] = e.[UserId]
    WHERE (@userId IS NULL OR e.[UserId] = @userId)
      AND (@visitor IS NULL OR e.[Visitor] = @visitor)
      AND (@kind IS NULL OR e.[Kind] = @kind)
      AND (@service IS NULL OR e.[Service] = @service)
      AND (@aiProvider IS NULL OR (e.[Service] = N'ai' AND e.[Subject] LIKE @aiProvider))
      AND (@aiModel IS NULL OR (e.[Service] = N'ai' AND e.[Subject] = @aiModel))
      AND (@members IS NULL OR (@members = 1 AND e.[UserId] IS NOT NULL) OR (@members = 0 AND e.[UserId] IS NULL))
      AND (@since IS NULL OR e.[AtUtc] >= @since)
      AND (@until IS NULL OR e.[AtUtc] < @until)
      AND (@q IS NULL OR
           (e.[Action] LIKE @q ESCAPE '\' OR e.[Path] LIKE @q ESCAPE '\' OR e.[Page] LIKE @q ESCAPE '\'
            OR e.[Subject] LIKE @q ESCAPE '\' OR e.[Detail] LIKE @q ESCAPE '\' OR e.[Ip] LIKE @q ESCAPE '\'
            OR e.[Visitor] LIKE @q ESCAPE '\' OR u.[DisplayName] LIKE @q ESCAPE '\' OR u.[Email] LIKE @q ESCAPE '\'))
    ORDER BY e.[AtUtc] DESC, e.[Id] DESC
    OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
    OPTION (RECOMPILE);
END
