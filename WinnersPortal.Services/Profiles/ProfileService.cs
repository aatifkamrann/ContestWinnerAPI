using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The portfolio a member keeps, and the merit score read off it.
///
/// Two shapes, and the difference matters: <c>/api/profile</c> is your own
/// and comes back editable; <c>/api/profile/{id}</c> is somebody else's and
/// comes back as a page — same facts, no email, no edit affordances. Both
/// carry the merit breakdown, because a score whose arithmetic is hidden is
/// a score nobody can argue with, and this one should be arguable.
///
/// One list is not the same on both: payment details come back only to the
/// member, an administrator or a client who has announced them as a winner
/// (ProfileRules.MayReadPayments), and to everybody else the field is absent
/// rather than empty — a freelancer reading a rival's page is not told there
/// is something they cannot see.
/// </summary>
public sealed class ProfileService(AppDbContext db, GitHubService github, PaymentSecrets secrets, AiOptions ai, AiWorkSignal aiSignal, IdentityOptions identity)
{
    public async Task<Outcome> AvatarAsync(Guid id, CancellationToken ct)
    {
        var avatar = await db.UserAvatars.AsNoTracking()
            .Where(a => a.UserId == id)
            .Select(a => new { a.Bytes, a.ContentType, a.UpdatedAtUtc })
            .SingleOrDefaultAsync(ct);
        if (avatar is null) return Outcome.NotFound();
        return Outcome.Bytes(
            avatar.Bytes, avatar.ContentType,
            lastModified: avatar.UpdatedAtUtc,
            etag: $"\"{avatar.UpdatedAtUtc.ToUnixTimeSeconds()}\"");
    }

    public async Task<Outcome> ProjectImageAsync(Guid id, CancellationToken ct)
    {
        var image = await db.ProfileImages.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Bytes, x.ContentType, x.UploadedAtUtc })
            .SingleOrDefaultAsync(ct);
        if (image is null) return Outcome.NotFound();
        return Outcome.Bytes(
            image.Bytes, image.ContentType,
            lastModified: image.UploadedAtUtc,
            etag: $"\"{image.UploadedAtUtc.ToUnixTimeSeconds()}\"");
    }

    public async Task<Outcome<ProfileView>> OwnAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        return Outcome.Ok((await ReadAsync(
            db, secrets, identity, id, id, Principal.Role(principal), await github.CanConnectAsync(ct), ct))!);
    }

    public async Task<Outcome<WelcomeView>> WelcomeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var welcome = await Welcome.ReadAsync(db, ai, aiSignal, Principal.UserId(principal)!.Value, ct);
        return welcome is null ? Outcome.NotFound() : Outcome.Ok(welcome);
    }

    public async Task<Outcome<ProfileView>> SaveAsync(ProfileRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = Principal.UserId(principal)!.Value;
        var skills = request.Skills ?? [];
        var languages = request.Languages ?? [];
        var projects = request.Projects ?? [];
        var problem = ProfileRules.Problem(
            [.. skills.Select(s => s.Name)],
            [.. languages.Select(l => l.Name)],
            [.. projects.Select(p => p.Title)]);
        if (problem is not null) return Outcome.Invalid(problem);
        // Counted before anything is decoded: a project that sends five
        // pictures is refused by the number, not by the megabytes.
        foreach (var sent in projects)
            if (sent.Images is { Count: > ProjectImageRules.MaxPerProject })
                return Outcome.Invalid(ProjectImageRules.TooMany(sent.Title));

        // Payments are cleaned before they are judged: the method decides
        // which fields exist at all, so an unknown method has to be
        // refused here rather than stored as a row nothing can render.
        List<(PaymentMethod Method, string? Label, Dictionary<string, string> Details)>? payments = null;
        if (request.Payments is not null)
        {
            payments = [];
            foreach (var sent in request.Payments)
            {
                var method = PaymentMethods.Find(sent.Method);
                if (method is null)
                    return Outcome.Invalid("Pick how you want to be paid.");
                payments.Add((
                    method,
                    ProfileRules.Clean(sent.Label, ProfileRules.MaxPaymentLabel),
                    PaymentMethods.CleanDetails(method, sent.Details)));
            }
            var paymentProblem = PaymentMethods.Problem(
                [.. payments.Select(p => (p.Method, p.Details))]);
            if (paymentProblem is not null) return Outcome.Invalid(paymentProblem);
        }

        // The display name lives on the account, not the profile, but it
        // is the first field of the same form — so it is saved with it
        // rather than behind a second button that could half-succeed.
        // Absent means unchanged: a client that does not know about this
        // field must not be able to blank somebody's name by omission.
        var account = await db.Users.SingleAsync(u => u.Id == id, ct);
        if (!ProfileRules.HasPayments(account.Role)) payments = null;
        if (request.DisplayName is not null)
        {
            var (name, nameProblem) = AccountRules.CleanDisplayName(request.DisplayName);
            if (nameProblem is not null) return Outcome.Invalid(nameProblem);
            account.DisplayName = name;
        }

        var profile = await db.Profiles
            .Include(p => p.Skills)
            .Include(p => p.Languages)
            .Include(p => p.Projects)
            .Include(p => p.Payments)
            .SingleOrDefaultAsync(p => p.UserId == id, ct);
        if (profile is null)
        {
            // Created on first save, not at registration: an account with
            // no profile is the normal starting state.
            profile = new Profile { UserId = id };
            db.Profiles.Add(profile);
        }
        else
        {
            db.ProfileSkills.RemoveRange(profile.Skills);
            db.ProfileLanguages.RemoveRange(profile.Languages);
            db.ProfileProjects.RemoveRange(profile.Projects);
            profile.Skills.Clear();
            profile.Languages.Clear();
            profile.Projects.Clear();
            // Payments are replaced only when the form sent a list. Every
            // other list here is cleared by omission; this one is not,
            // because it is the one list a screen might legitimately not
            // be showing, and losing an account number by omission is
            // worse than the inconsistency.
            if (payments is not null)
            {
                db.ProfilePayments.RemoveRange(profile.Payments);
                profile.Payments.Clear();
            }
        }

        // The picture rides the same save as the rest of the form, after
        // every check that could refuse it. Absent leaves it alone; remove
        // clears it; bytes replace it — and the bytes say what they are,
        // the declared type is never read (AvatarRules.Read).
        if (request.Avatar is { } picture)
        {
            var stored = await db.UserAvatars.SingleOrDefaultAsync(a => a.UserId == id, ct);
            if (picture.Remove)
            {
                if (stored is not null) db.UserAvatars.Remove(stored);
                account.AvatarUpdatedAtUtc = null;
            }
            else
            {
                var (bytes, contentType, pictureProblem) = AvatarRules.Read(picture.DataBase64);
                if (pictureProblem is not null) return Outcome.Invalid(pictureProblem);
                var setAt = DateTimeOffset.UtcNow;
                if (stored is null)
                    db.UserAvatars.Add(new UserAvatar
                    {
                        UserId = id, Bytes = bytes!, ContentType = contentType!, UpdatedAtUtc = setAt,
                    });
                else
                {
                    stored.Bytes = bytes!;
                    stored.ContentType = contentType!;
                    stored.UpdatedAtUtc = setAt;
                }
                account.AvatarUpdatedAtUtc = setAt;
            }
        }

        // Pictures of past work, which do not ride the same rule as
        // the lists around them: every other list here is replaced
        // wholesale on every save, and bytes re-sent each time somebody
        // fixed a typo would be the most expensive field on the form. So
        // a picture already stored travels back as its id and is kept,
        // and only a new one travels as bytes. Anything no project still
        // points at is deleted below, in the same save — taking a
        // picture off the form is what deletes it.
        var storedImages = await db.ProfileImages.Where(x => x.UserId == id).ToListAsync(ct);
        var ownImageIds = storedImages.Select(x => x.Id).ToHashSet();
        var newImages = new List<ProfileImage>();
        var keptImageIds = new HashSet<Guid>();
        var projectImageIds = new List<List<Guid>>(projects.Count);
        var newImageBytes = 0L;
        var uploadedAt = DateTimeOffset.UtcNow;
        foreach (var sent in projects)
        {
            var ids = new List<Guid>();
            foreach (var shot in sent.Images ?? [])
            {
                if (shot.Id is { } existing)
                {
                    // Somebody else's id is not a picture this member may
                    // show, and the same picture on two projects is one
                    // picture: both are dropped rather than refused,
                    // because neither is something a member did.
                    if (ownImageIds.Contains(existing) && keptImageIds.Add(existing)) ids.Add(existing);
                    continue;
                }
                var (bytes, contentType, pictureProblem) = ImageRules.Read(
                    shot.DataBase64, ProjectImageRules.MaxBytes, ProjectImageRules.TooBig,
                    "One of the pictures was empty — choose it again.");
                if (pictureProblem is not null) return Outcome.Invalid(pictureProblem);
                newImageBytes += bytes!.Length;
                if (newImageBytes > ProjectImageRules.MaxNewBytesPerSave)
                    return Outcome.Invalid(ProjectImageRules.TooMuchAtOnce);
                var row = new ProfileImage
                {
                    Id = Guid.NewGuid(),
                    UserId = id,
                    Bytes = bytes!,
                    ContentType = contentType!,
                    UploadedAtUtc = uploadedAt,
                };
                newImages.Add(row);
                ids.Add(row.Id);
            }
            projectImageIds.Add(ids);
        }

        profile.Headline = ProfileRules.Clean(request.Headline, ProfileRules.MaxHeadline);
        profile.Bio = ProfileRules.Clean(request.Bio, ProfileRules.MaxBio);
        // One kind of work and up to three more, every key checked
        // against the taxonomy a brief is filed under. Unknown keys are
        // dropped rather than refused: a category the registry retires
        // must not make somebody's profile unsaveable.
        var (primaryCategory, secondaryCategories) =
            ProfileRules.CleanCategories(request.PrimaryCategory, request.SecondaryCategories);
        profile.PrimaryCategory = primaryCategory;
        profile.SecondaryCategories = secondaryCategories;
        profile.PrimaryWorkType = ProfileRules.ParseWorkType(request.WorkType);
        profile.Location = ProfileRules.Clean(request.Location, ProfileRules.MaxLocation);
        profile.TimeZone = ProfileRules.Clean(request.TimeZone, ProfileRules.MaxTimeZone);
        profile.YearsExperience = ProfileRules.CleanCount(request.YearsExperience, ProfileRules.MaxYearsExperience);
        profile.HoursPerWeek = ProfileRules.CleanCount(request.HoursPerWeek, ProfileRules.MaxHoursPerWeek);
        // When they can work: a status word, two sets of keys from the
        // closed lists, and a window as they wrote it. Unknown words and
        // keys fall out on the way in, as the categories' do.
        profile.Availability = ProfileRules.ParseAvailability(request.Availability);
        profile.PreferredDurations =
            ProfileRules.CleanPreferences(request.PreferredDurations, ProjectDurations.All);
        profile.PreferredProjectTypes =
            ProfileRules.CleanPreferences(request.PreferredProjectTypes, EngagementTypes.All);
        profile.WorkingWindow = ProfileRules.Clean(request.WorkingWindow, ProfileRules.MaxWorkingWindow);
        profile.WebsiteUrl = ProfileRules.CleanUrl(request.WebsiteUrl);
        profile.LinkedInUrl = ProfileRules.CleanUrl(request.LinkedInUrl);
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var thisYear = DateTimeOffset.UtcNow.Year;
        profile.Skills.AddRange(skills.Select((s, i) => new ProfileSkill
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Order = i,
            Name = ProfileRules.Clean(s.Name, ProfileRules.MaxSkillName)!,
            Years = ProfileRules.CleanCount(s.Years, ProfileRules.MaxSkillYears) ?? 0,
            Level = ProfileRules.ParseLevel(s.Level),
        }));
        profile.Languages.AddRange(languages.Select((l, i) => new ProfileLanguage
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Order = i,
            Name = ProfileRules.Clean(l.Name, ProfileRules.MaxLanguageName)!,
            Level = ProfileRules.ParseLanguageLevel(l.Level),
        }));
        profile.Projects.AddRange(projects.Select((p, i) =>
        {
            var year = ProfileRules.CleanYear(p.Year, thisYear);
            return new ProfileProject
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Order = i,
                Title = ProfileRules.Clean(p.Title, ProfileRules.MaxProjectTitle)!,
                Description = ProfileRules.Clean(p.Description, ProfileRules.MaxProjectDescription),
                Outcome = ProfileRules.Clean(p.Outcome, ProfileRules.MaxProjectOutcome),
                Role = ProfileRules.Clean(p.Role, ProfileRules.MaxProjectRole),
                // The same taxonomy the member's own kinds of work come
                // from, cleaned the same way: a key it no longer knows
                // leaves the field empty rather than the form unsaveable.
                Category = ProfileRules.CleanCategory(p.Category),
                // One text box holding a list: a repeat inside it has no row
                // to point at, so it is dropped rather than refused.
                Tech = ProfileRules.DedupeList(p.Tech, ProfileRules.MaxProjectTech),
                Url = ProfileRules.CleanUrl(p.Url),
                RepoUrl = ProfileRules.CleanUrl(p.RepoUrl),
                Year = year,
                Month = ProfileRules.CleanMonth(p.Month, year),
                MayShowPublicly = p.MayShowPublicly,
                ImageIds = projectImageIds[i],
            };
        }));
        if (payments is not null)
            profile.Payments.AddRange(payments.Select((p, i) => new ProfilePayment
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Order = i,
                Method = p.Method.Key,
                Label = p.Label,
                Details = secrets.Protect(p.Details),
            }));
        // Explicitly Added: reached by navigation from a tracked parent,
        // EF would otherwise guess these client-keyed rows are Modified.
        db.ProfileSkills.AddRange(profile.Skills);
        db.ProfileLanguages.AddRange(profile.Languages);
        db.ProfileProjects.AddRange(profile.Projects);
        if (payments is not null) db.ProfilePayments.AddRange(profile.Payments);
        db.ProfileImages.AddRange(newImages);
        // The removal is the same save as the rest: a picture is gone
        // from the store exactly when it is gone from the form, and a
        // save that is refused above leaves both alone.
        db.ProfileImages.RemoveRange(storedImages.Where(x => !keptImageIds.Contains(x.Id)));

        await db.SaveChangesAsync(ct);

        // The token carries the name as a claim, and one audit line
        // reads it. The cookie is re-issued on the same keep-me-signed-in
        // choice, so a rename does not quietly turn "remember me" off; a
        // bearer token picks the name up at its next refresh.
        return Outcome.Ok((await ReadAsync(
            db, secrets, identity, id, id, Principal.Role(principal), await github.CanConnectAsync(ct), ct))!)
            .WithRefreshed(account);
    }

    public async Task<Outcome<ProfileSuggestionsResponse>> SuggestionsAsync(CancellationToken ct)
    {
        // Read through the profile rather than off the child tables, so
        // a deleted member's city and vocabulary leave with them: the
        // filter that hides their profile hides everything reached
        // through it, and the skill and project tables have no filter of
        // their own to remember.
        var profiles = await db.Profiles.AsNoTracking()
            .Select(p => new { p.Location, p.TimeZone, p.WorkingWindow })
            .ToListAsync(ct);
        var skills = await db.Profiles.AsNoTracking()
            .SelectMany(p => p.Skills).Select(s => s.Name).ToListAsync(ct);
        var languages = await db.Profiles.AsNoTracking()
            .SelectMany(p => p.Languages).Select(l => l.Name).ToListAsync(ct);
        var projects = await db.Profiles.AsNoTracking()
            .SelectMany(p => p.Projects)
            .Select(x => new { x.Role, x.Tech })
            .ToListAsync(ct);
        return Outcome.Ok(new ProfileSuggestionsResponse
        {
            Locations = ProfileRules.Suggest(profiles.Select(p => p.Location), split: false),
            TimeZones = ProfileRules.Suggest(profiles.Select(p => p.TimeZone), split: false),
            Languages = ProfileRules.Suggest(languages, split: false),
            // A skill and a project's "built with" are the same vocabulary.
            Skills = ProfileRules.Suggest(skills, split: false)
                .Concat(ProfileRules.Suggest(projects.Select(x => x.Tech), split: true))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(ProfileRules.MaxSuggestions)
                .ToList(),
            Roles = ProfileRules.Suggest(projects.Select(x => x.Role), split: false),
            WorkingWindows = ProfileRules.Suggest(profiles.Select(p => p.WorkingWindow), split: false),
        });
    }

    public async Task<Outcome<ProfileView>> MemberAsync(Guid userId, ClaimsPrincipal principal, CancellationToken ct)
    {
        var result = await ReadAsync(
            db, secrets, identity, userId, Principal.UserId(principal), Principal.Role(principal),
            await github.CanConnectAsync(ct), ct);
        return result is null ? Outcome.NotFound() : Outcome.Ok(result);
    }

    /// <summary>
    /// One person's profile and their merit score. Null only when the
    /// account does not exist — an account with no profile row still has a
    /// record, and a score, and is worth showing.
    /// </summary>
    internal static async Task<ProfileView?> ReadAsync(
        AppDbContext db, PaymentSecrets secrets, IdentityOptions identity, Guid userId,
        Guid? viewerId, string? viewerRole, bool githubOffered, CancellationToken ct)
    {
        var own = viewerId == userId;
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.ErasedAtUtc == null)
            .Select(u => new
            {
                u.Id, u.DisplayName, u.Role, u.GithubLogin, u.CreatedAtUtc, u.AvatarUpdatedAtUtc,
                u.EmailConfirmedAtUtc, u.PhoneConfirmedAtUtc, u.IdentityVerifiedAtUtc,
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) return null;

        // The award first, then the portal's door on top of it. A client
        // reads the details once they have announced this member as a
        // winner, and a client who has not is told they will; either is
        // held back until the member has verified, and told so. A rival
        // freelancer is told nothing.
        var clientReading = !own && viewerRole == Roles.Client && viewerId is not null
            && account.Role == Roles.Freelancer;
        var awardedByViewer = clientReading && await db.Awards.AnyAsync(
            a => a.Entry!.FreelancerId == userId && a.Opportunity!.ClientId == viewerId, ct);
        var mayReadByAward = ProfileRules.HasPayments(account.Role)
            && ProfileRules.MayReadPayments(own, viewerRole, awardedByViewer);
        var paymentsWithheld = mayReadByAward && IdentityRules.PaymentsWithheld(
            await identity.IsEnabledAsync(ct), await identity.RequiredForPaymentsAsync(ct),
            own, viewerRole, account.IdentityVerifiedAtUtc is not null);
        var mayReadPayments = mayReadByAward && !paymentsWithheld;
        var paymentsNote =
            paymentsWithheld ? IdentityRules.PaymentsWithheldNote(account.DisplayName)
            : clientReading && !awardedByViewer ? ProfileRules.PaymentsAfterAwardNote(account.DisplayName)
            : null;
        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new
            {
                p.Headline, p.Bio, p.Location, p.TimeZone,
                p.PrimaryCategory, p.SecondaryCategories, p.PrimaryWorkType,
                p.YearsExperience, p.HoursPerWeek, p.WebsiteUrl, p.LinkedInUrl, p.UpdatedAtUtc,
                p.Availability, p.PreferredDurations, p.PreferredProjectTypes, p.WorkingWindow,
                Skills = p.Skills.OrderBy(s => s.Order)
                    .Select(s => new { s.Name, s.Years, s.Level }).ToList(),
                Languages = p.Languages.OrderBy(l => l.Order)
                    .Select(l => new { l.Name, l.Level }).ToList(),
                Projects = p.Projects.OrderBy(x => x.Order)
                    .Select(x => new
                    {
                        x.Title, x.Description, x.Outcome, x.Role, x.Category, x.Tech,
                        x.Url, x.RepoUrl, x.Year, x.Month, x.MayShowPublicly, x.ImageIds,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);

        // A query of its own, and only when it is allowed to be: the
        // ciphertext is never read for a viewer who may not have the
        // plaintext, so there is nothing to forget to leave out later.
        var paymentRows = mayReadPayments
            ? await db.ProfilePayments.AsNoTracking()
                .Where(x => x.UserId == userId)
                .OrderBy(x => x.Order)
                .Select(x => new { x.Method, x.Label, x.Details })
                .ToListAsync(ct)
            : null;

        var record = await MeritReader.RecordAsync(db, userId, ct);
        var portfolio = new Merit.Portfolio(
            HasHeadline: !string.IsNullOrWhiteSpace(profile?.Headline),
            HasBio: !string.IsNullOrWhiteSpace(profile?.Bio),
            Skills: profile?.Skills.Count ?? 0,
            Projects: profile?.Projects.Count ?? 0,
            ProjectsWithLinks: profile?.Projects.Count(x => x.Url != null || x.RepoUrl != null) ?? 0,
            HasDetails: profile is not null
                && !string.IsNullOrWhiteSpace(profile.Location)
                && profile.HoursPerWeek is > 0
                && profile.YearsExperience is not null,
            GithubConnected: account.GithubLogin is not null,
            GithubOffered: githubOffered);

        var score = Merit.Score(portfolio, record);

        // The member's own checklist, and only theirs: a tick on Wallet
        // would tell a rival there is something there, which is exactly
        // what the payments rule says nobody else is told.
        var strength = own
            ? new Strength.Facts(
                Confirmed: account.EmailConfirmedAtUtc is not null || account.PhoneConfirmedAtUtc is not null,
                HasHeadline: portfolio.HasHeadline,
                HasBio: portfolio.HasBio,
                HasCategory: OpportunityCategories.Find(profile?.PrimaryCategory) is not null,
                Skills: portfolio.Skills,
                Projects: portfolio.Projects,
                HasAvailability: profile?.Availability is not null,
                Payments: paymentRows?.Count ?? 0)
            : null;

        return new ProfileView
        {
            UserId = account.Id,
            DisplayName = account.DisplayName,
            AvatarUrl = AvatarRules.Url(account.Id, account.AvatarUpdatedAtUtc),
            Role = account.Role,
            GithubLogin = account.GithubLogin,
            // Whether the connect round trip exists on this portal at all —
            // the profile shows the button on that, and the merit breakdown
            // drops the GitHub line's two points where it does not.
            GithubConnectable = githubOffered,
            MemberSince = account.CreatedAtUtc,
            Own = own,
            Headline = profile?.Headline,
            Bio = profile?.Bio,
            // Key and label together, the way an opportunity hands out its own
            // category: the page that renders a profile is not always the
            // page that has loaded the taxonomy.
            PrimaryCategory = Category(profile?.PrimaryCategory),
            SecondaryCategories = (profile?.SecondaryCategories ?? [])
                .Select(Category).Where(c => c is not null),
            WorkType = ProfileRules.WorkTypeName(profile?.PrimaryWorkType),
            Location = profile?.Location,
            TimeZone = profile?.TimeZone,
            YearsExperience = profile?.YearsExperience,
            HoursPerWeek = profile?.HoursPerWeek,
            // Keys, as the work type travels: the form holds its own words
            // for these three short lists, the way it does for the levels.
            Availability = ProfileRules.AvailabilityName(profile?.Availability),
            PreferredDurations = profile?.PreferredDurations ?? [],
            PreferredProjectTypes = profile?.PreferredProjectTypes ?? [],
            WorkingWindow = profile?.WorkingWindow,
            WebsiteUrl = profile?.WebsiteUrl,
            LinkedInUrl = profile?.LinkedInUrl,
            UpdatedAtUtc = profile?.UpdatedAtUtc,
            Skills = profile?.Skills.Select(s => new ProfileSkillView
            {
                Name = s.Name,
                Years = s.Years,
                Level = ProfileRules.LevelName(s.Level),
            }) ?? [],
            Languages = profile?.Languages.Select(l => new ProfileLanguageView
            {
                Name = l.Name,
                Level = ProfileRules.LanguageLevelName(l.Level),
            }) ?? [],
            // Work the member has not said they may show publicly is
            // theirs alone: it stays on their own copy of the page, marked,
            // and is not in anybody else's copy at all. The merit score
            // above counts it either way — the score is what this person has
            // done, and the list is what they are allowed to show.
            Projects = profile?.Projects.Where(x => own || x.MayShowPublicly).Select(x => new ProfileProjectView
            {
                Title = x.Title, Description = x.Description, Outcome = x.Outcome, Role = x.Role,
                Category = Category(x.Category),
                Tech = x.Tech, Url = x.Url, RepoUrl = x.RepoUrl,
                Year = x.Year, Month = x.Month,
                MayShowPublicly = x.MayShowPublicly,
                // Id and address together: the address is what the page
                // renders, and the id is what the form sends back to say
                // "this one is already yours, keep it".
                Images = x.ImageIds.Select(image => new ProjectImageView
                {
                    Id = image,
                    Url = ProjectImageRules.Url(image),
                }),
            }) ?? [],
            // Null, not an empty list: a viewer who may not read these is not
            // told whether there was anything to read.
            Payments = paymentRows?
                .Select(x => Payment(secrets, x.Method, x.Label, x.Details))
                .Where(x => x is not null)
                .ToList(),
            PaymentsNote = paymentsNote,
            IdentityVerifiedAtUtc = account.IdentityVerifiedAtUtc,
            Strength = strength is null
                ? null
                : new ProfileStrength
                {
                    Percent = Strength.Percent(strength),
                    Steps = Strength.Steps(strength)
                        .Select(s => new StrengthStep { Label = s.Label, Done = s.Done, Detail = s.Detail }),
                },
            Merit = new MeritView
            {
                Score = score,
                Max = Merit.MaxFor(portfolio),
                Band = Merit.Band(score),
                Completeness = Merit.Completeness(portfolio),
                Portfolio = new MeritHalf
                {
                    Earned = Merit.PortfolioScore(portfolio),
                    Available = Merit.PortfolioAvailable(portfolio),
                    Parts = Merit.PortfolioParts(portfolio).Select(Line),
                },
                Record = new MeritHalf
                {
                    Earned = Merit.RecordScore(record),
                    Available = Merit.RecordMax,
                    Parts = Merit.RecordParts(record).Select(Line),
                },
            },
        };
    }

    /// <summary>One kind of work as the page reads it: the stored key, and the name for it.</summary>
    private static ProfileCategoryView? Category(string? key) =>
        OpportunityCategories.Find(key) is { } found
            ? new ProfileCategoryView { Key = found.Key, Label = found.Label }
            : null;

    /// <summary>
    /// One stored payment method, decrypted and labelled in the registry's
    /// order so the page that renders it needs no catalogue of its own. Null
    /// for a row whose method the portal no longer knows; a row that cannot
    /// be decrypted comes back with no values and says so, because the only
    /// useful answer is for its owner to type it again.
    /// </summary>
    private static PaymentView? Payment(PaymentSecrets secrets, string methodKey, string? label, string stored)
    {
        var method = PaymentMethods.Find(methodKey);
        if (method is null) return null;
        var details = secrets.Unprotect(stored);
        return new PaymentView
        {
            Method = method.Key,
            MethodLabel = method.Label,
            Label = label,
            Unreadable = details is null,
            Details = method.Fields
                .Where(f => details is not null && details.ContainsKey(f.Key))
                .Select(f => new PaymentDetail { Key = f.Key, Label = f.Label, Value = details![f.Key] }),
        };
    }

    private static MeritLine Line(Merit.Part p) =>
        new MeritLine { Label = p.Label, Earned = p.Earned, Available = p.Available, Detail = p.Detail };
}

public sealed record ProfileRequest(
    /// <summary>The account's own display name. Null leaves it alone.</summary>
    string? DisplayName,
    /// <summary>The picture beside it. Null leaves it alone; see <see cref="AvatarInput"/>.</summary>
    AvatarInput? Avatar,
    string? Headline,
    string? Bio,
    /// <summary>The kind of work they mainly do — a key from the opportunity taxonomy.</summary>
    string? PrimaryCategory,
    /// <summary>Up to three more. Anything the taxonomy does not know is dropped.</summary>
    List<string>? SecondaryCategories,
    /// <summary>How they work, as its word — "fulltime", "parttime", "independent", "agency".</summary>
    string? WorkType,
    string? Location,
    string? TimeZone,
    int? YearsExperience,
    int? HoursPerWeek,
    /// <summary>Whether they can start, as its word — "now", "week", "twoweeks", "unavailable".</summary>
    string? Availability,
    /// <summary>Keys from <c>ProjectDurations.All</c>; anything else is dropped.</summary>
    List<string>? PreferredDurations,
    /// <summary>Keys from <c>EngagementTypes.All</c>; anything else is dropped.</summary>
    List<string>? PreferredProjectTypes,
    string? WorkingWindow,
    string? WebsiteUrl,
    string? LinkedInUrl,
    List<SkillInput>? Skills,
    List<LanguageInput>? Languages,
    List<ProjectInput>? Projects,
    /// <summary>Null leaves the stored ways of being paid alone; an empty list clears them.</summary>
    List<PaymentInput>? Payments);

// Level is the word, not the number. The enum has no string converter
// registered anywhere in this API, so a bare SkillLevel here would accept
// only 0 to 3 -- while every read of a profile hands out "beginner",
// "intermediate", "advanced" or "expert". The form sends back what it was
// given. The work type above travels the same way, for the same reason.
public sealed record SkillInput(string? Name, int? Years, string? Level);

/// <summary>The same wire rule as a skill: the level travels as its word.</summary>
public sealed record LanguageInput(string? Name, string? Level);

/// <summary>
/// One way of being paid. The details are keyed by the field keys the method
/// declared; anything else in the dictionary is dropped before it is stored.
/// </summary>
public sealed record PaymentInput(string? Method, string? Label, Dictionary<string, string?>? Details);

/// <summary>
/// A change to the picture. Either <c>remove</c>, or the picture as base64
/// (a <c>data:</c> URL from a canvas is fine). No declared type: the
/// bytes are read for what they are.
/// </summary>
public sealed record AvatarInput(string? DataBase64, bool Remove);

public sealed record ProjectInput(
    string? Title,
    string? Description,
    /// <summary>What came of it: the result or the benefit.</summary>
    string? Outcome,
    string? Role,
    /// <summary>Which kind of work it was — a key from the opportunity taxonomy, as the profile's own are.</summary>
    string? Category,
    string? Tech,
    string? Url,
    string? RepoUrl,
    int? Year,
    /// <summary>1-12, beside the year. Dropped without one — half a date is not a date.</summary>
    int? Month,
    /// <summary>
    /// The member saying they may show this work publicly. Absent is not
    /// permission: false keeps the row on their own page and off everybody
    /// else's, which is what the portal should do when nobody has said.
    /// </summary>
    bool MayShowPublicly,
    List<ProjectImageInput>? Images);

/// <summary>
/// One picture on a project: either the id of one already stored — which
/// keeps it, in this position — or a new one as the base64 the browser
/// shrank it to. An id this member does not own is dropped rather than
/// refused: it is not something they did.
/// </summary>
public sealed record ProjectImageInput(Guid? Id, string? DataBase64);
