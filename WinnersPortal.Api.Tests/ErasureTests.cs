using WinnersPortal.Services.Ai;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Services.Auth;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Help;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// What "delete this account" actually takes, and what it must leave. The
/// decision pinned here is where the line falls: a record is something
/// somebody else can be owed — an opportunity, an entry, an award, a rating —
/// and stays; everything the person gave the portal is wiped; and the
/// profile they wrote is closed rather than dropped, kept as the audit of
/// a deletion nobody could otherwise account for, and unreadable from the
/// moment it is marked.
///
/// Half of this is arithmetic on a row (<see cref="Erasure.Scrub"/>,
/// <see cref="Erasure.MarkProfileDeleted"/>) and half is the shape of the
/// database, because "a closed profile is out of every query" and "an
/// audit trail never blocks a deletion" are both claims about the model.
/// Both halves are checked without a database: the model is built from
/// AppDbContext, never connected to.
/// </summary>
public class ErasureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    /// <summary>
    /// Columns an erasure deliberately keeps. Each one is either the record
    /// itself or the hook a record hangs on: nothing here says who the
    /// person was.
    /// </summary>
    private static readonly string[] Kept =
    [
        nameof(User.Id),
        nameof(User.Role),
        nameof(User.CreatedAtUtc),
        // Consent given is a fact about the account, and evidence.
        nameof(User.AcceptedTermsVersion),
        nameof(User.AcceptedTermsAtUtc),
        // That contact was once proved. The address and number themselves go.
        nameof(User.EmailConfirmedAtUtc),
        nameof(User.PhoneConfirmedAtUtc),
        // Sums over records that still exist; zeroing them would falsify a
        // feed card, not protect anybody.
        nameof(User.AwardsPaidCount),
        nameof(User.RatingCount),
        nameof(User.RatingSum),
    ];

    /// <summary>Columns an erasure overwrites rather than blanks; each is checked by name below.</summary>
    private static readonly string[] Stamped =
    [
        nameof(User.Email),
        nameof(User.DisplayName),
        nameof(User.PasswordHash),
        nameof(User.LockedAtUtc),
        nameof(User.LockReason),
        nameof(User.ErasedAtUtc),
        nameof(User.SessionStamp),
    ];

    // ------------------------------------------------------------ the row

    [Fact]
    public void Every_column_that_is_the_person_is_cleared()
    {
        // The point of doing this by reflection: a column added later —
        // another address, another handle, another preference — fails here
        // until somebody decides which side of the line it is on. That is
        // exactly how a phone number came to outlive an erasure once.
        var user = Filled();
        var before = Snapshot(user);

        Erasure.Scrub(user, Now);

        foreach (var column in Columns.Where(c => !Kept.Contains(c.Name) && !Stamped.Contains(c.Name)))
            Assert.True(
                Equals(column.GetValue(user), Blank(column.PropertyType)),
                $"User.{column.Name} survives an erasure. Clear it in Erasure.Scrub, or, if it is a record "
                + "rather than the person, add it to ErasureTests.Kept and say why.");

        foreach (var column in Columns.Where(c => Kept.Contains(c.Name)))
            Assert.True(
                Equals(column.GetValue(user), before[column.Name]),
                $"User.{column.Name} is a record an erasure must keep, and something changed it.");
    }

    [Fact]
    public void The_row_that_is_left_is_a_record_with_nobody_in_it()
    {
        var user = Filled();
        var id = user.Id;
        var stamp = user.SessionStamp;

        Erasure.Scrub(user, Now);

        // The id every opportunity, entry, award and rating points at.
        Assert.Equal(id, user.Id);
        // An address that is unique, undeliverable, and cannot collide with
        // a live account — the create and rename checks look at every row.
        Assert.Equal(AccountRules.ErasedEmail(id), user.Email);
        Assert.EndsWith("@erased.invalid", user.Email);
        Assert.Equal(AccountRules.ErasedName, user.DisplayName);
        Assert.Equal("", user.PasswordHash);
        Assert.Equal(Now, user.ErasedAtUtc);
        Assert.Equal("Deleted", user.LockReason);
        Assert.False(AccountRules.CanSignIn(user));
        // Every session the account held ends on its next request.
        Assert.NotEqual(stamp, user.SessionStamp);
    }

    [Fact]
    public void An_account_already_locked_keeps_the_day_it_was_locked()
    {
        // The lock date is when somebody was shut out, which is a truer
        // record than the day the paperwork caught up.
        var locked = Now.AddDays(-9);
        var user = Filled();
        user.LockedAtUtc = locked;

        Erasure.Scrub(user, Now);

        Assert.Equal(locked, user.LockedAtUtc);
        Assert.Equal(Now, user.ErasedAtUtc);
    }

    // -------------------------------------------------------- the profile

    [Fact]
    public void A_deleted_profile_is_marked_with_who_did_it_and_when()
    {
        var profile = new Profile { UserId = Guid.NewGuid(), Headline = "Django specialist" };
        var admin = Guid.NewGuid();

        Erasure.MarkProfileDeleted(profile, Now, admin);

        Assert.True(profile.IsDeleted);
        Assert.Equal(Now, profile.DeletedAtUtc);
        Assert.Equal(admin, profile.DeletedByUserId);
        // The words themselves are left alone. Blanking them here would
        // leave a row that records a deletion of nothing in particular,
        // which is the one thing an audit trail must not be.
        Assert.Equal("Django specialist", profile.Headline);
    }

    [Fact]
    public void A_deleted_profile_is_out_of_every_query_the_portal_makes()
    {
        // The whole basis for keeping the row: the content stays in the
        // table and leaves the portal. A filter here is worth more than a
        // filter at each read site, because the profile is read by the
        // profile page, the entrant list, the standing board, the merit
        // score and the suggestion lists — and the erased-account bug this
        // replaced was two of those forgetting.
        using var db = Model();
        var filter = db.Model.FindEntityType(typeof(Profile))!.GetQueryFilter();

        Assert.NotNull(filter);
        Assert.Contains(nameof(Profile.IsDeleted), filter.ToString());
    }

    [Fact]
    public void An_audit_trail_never_blocks_a_deletion()
    {
        // The administrator who deleted somebody may be deleted themselves
        // later. Restrict here would make that second deletion fail on a
        // foreign key; the audit gives up a name instead.
        using var db = Model();
        var byUser = db.Model.FindEntityType(typeof(Profile))!.GetForeignKeys()
            .Single(f => f.Properties.Any(p => p.Name == nameof(Profile.DeletedByUserId)));

        Assert.Equal(DeleteBehavior.SetNull, byUser.DeleteBehavior);
    }

    [Fact]
    public void An_account_nobody_can_be_owed_by_takes_its_profile_with_it()
    {
        // The other outcome, where there is nothing to audit because the
        // account row itself is gone: the profile goes by cascade. If this
        // ever turns to Restrict, deleting an account with a profile starts
        // failing on a foreign key instead.
        using var db = Model();
        var toUser = db.Model.FindEntityType(typeof(Profile))!.GetForeignKeys()
            .Single(f => f.Properties.Any(p => p.Name == nameof(Profile.UserId)));

        Assert.Equal(DeleteBehavior.Cascade, toUser.DeleteBehavior);
    }

    [Theory]
    [InlineData(typeof(ProfileSkill))]
    [InlineData(typeof(ProfileLanguage))]
    [InlineData(typeof(ProfileProject))]
    [InlineData(typeof(ProfileImage))]
    [InlineData(typeof(ProfilePayment))]
    public void Everything_hanging_off_a_profile_goes_with_the_profile(Type list)
    {
        // These have no filter of their own, so nothing may read them
        // except through the profile — which is how the suggestion lists
        // read them — and nothing may outlive the profile row either.
        using var db = Model();
        var fk = db.Model.FindEntityType(list)!.GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(Profile));

        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Only_the_person_s_own_rows_follow_them_out()
    {
        // The whole decision in one assertion, for the outcome where the
        // account row itself goes: seven tables are the person's — their
        // portfolio, their devices, their picture, their merit score by
        // the day, their standing sign-ins, their identity verdict, their
        // inbox — and are deleted with them; every other table that names
        // a user is a record, and a record is somebody else's too.
        using var db = Model();
        var cascading = db.Model.GetEntityTypes()
            .SelectMany(t => t.GetForeignKeys())
            .Where(f => f.PrincipalEntityType.ClrType == typeof(User) && f.DeleteBehavior == DeleteBehavior.Cascade)
            .Select(f => f.DeclaringEntityType.ClrType.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [nameof(IdentityVerification), nameof(MeritSnapshot), nameof(Notification), nameof(Profile), nameof(PushDevice), nameof(RefreshToken), nameof(UserAvatar)],
            cascading);
    }

    // --------------------------------------------------------- the record

    [Theory]
    [InlineData(typeof(Opportunity))]
    [InlineData(typeof(Entry))]
    [InlineData(typeof(Rating))]
    public void A_record_never_follows_the_person_out(Type record)
    {
        // An opportunity posted, an entry made, a rating given or received: other
        // members' history as much as this one's. The database refuses to
        // delete them with the account, which is why "delete" erases in
        // place at all.
        using var db = Model();
        foreach (var fk in db.Model.FindEntityType(record)!.GetForeignKeys()
                     .Where(f => f.PrincipalEntityType.ClrType == typeof(User)))
            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void A_conversation_goes_with_its_entry_and_never_takes_an_account_with_it()
    {
        // The conversation is the entry's, so it goes where the entry goes.
        // Its sender's key refuses a deletion instead: erasure deletes the
        // person's own lines first and keeps the other side's, and an
        // account that said anything has an entry or an opportunity, so it
        // is erased in place and its row never leaves.
        using var db = Model();
        var keys = db.Model.FindEntityType(typeof(ChatMessage))!.GetForeignKeys().ToList();

        Assert.Equal(DeleteBehavior.Cascade,
            keys.Single(f => f.PrincipalEntityType.ClrType == typeof(Entry)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict,
            keys.Single(f => f.PrincipalEntityType.ClrType == typeof(User)).DeleteBehavior);
    }

    [Fact]
    public void A_report_goes_with_its_entry_and_its_reviewer_gives_up_a_name()
    {
        // A report is about the entry's conversation, so it goes with the
        // entry. Its reporter's key refuses a deletion, as a sender's does —
        // erasure deletes the person's own reports first. Its reviewer may
        // be deleted later; the report gives up the name, as a profile's
        // audit does (on SQL Server the portal nulls it at the removal site).
        using var db = Model();
        var keys = db.Model.FindEntityType(typeof(ChatReport))!.GetForeignKeys().ToList();

        Assert.Equal(DeleteBehavior.Cascade,
            keys.Single(f => f.PrincipalEntityType.ClrType == typeof(Entry)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict,
            keys.Single(f => f.Properties.Any(p => p.Name == nameof(ChatReport.ReporterId))).DeleteBehavior);
        Assert.Equal(DeleteBehavior.SetNull,
            keys.Single(f => f.Properties.Any(p => p.Name == nameof(ChatReport.ResolvedById))).DeleteBehavior);
    }

    [Fact]
    public void An_award_outlives_both_parties_to_it()
    {
        // The award hangs off the opportunity and the entry rather than a user,
        // so this is the same rule one table along: a paid award with no
        // winner on it is a dispute nobody can settle.
        using var db = Model();
        foreach (var fk in db.Model.FindEntityType(typeof(Award))!.GetForeignKeys())
            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void Deleting_an_account_with_history_is_never_offered()
    {
        // The counts the endpoint takes before it chooses which of the two
        // deletions this is. Any one of them is enough to erase in place.
        Assert.True(AccountRules.LeavesNoTrace(0, 0, 0));
        Assert.False(AccountRules.LeavesNoTrace(1, 0, 0));
        Assert.False(AccountRules.LeavesNoTrace(0, 1, 0));
        Assert.False(AccountRules.LeavesNoTrace(0, 0, 1));
    }

    // ----------------------------------------------------------- the help

    [Fact]
    public void The_help_an_administrator_reads_says_what_is_kept_and_what_is_not()
    {
        // An administrator clicks this on somebody else's behalf, and the
        // three outcomes are different: wiped, kept but unreadable, and
        // deleted outright. Text that blurs them is worse than none.
        var topic = HelpRegistry.Find("admin.deleteUser")!;
        var text = $"{topic.Short} {topic.Detail} {topic.Why}";

        foreach (var wiped in new[] { "phone", "password", "GitHub link", "devices" })
            Assert.Contains(wiped, text, StringComparison.OrdinalIgnoreCase);
        // The profile is kept and closed, and the text has to say both —
        // that it is no longer readable, and that the row is still there.
        foreach (var closed in new[] { "profile", "portfolio", "readable", "kept" })
            Assert.Contains(closed, text, StringComparison.OrdinalIgnoreCase);
        // Payment details are the one part of a profile that does not stay.
        Assert.Contains("payment details deleted", text, StringComparison.OrdinalIgnoreCase);
        // …and the record half, which is what erasing in place is for.
        foreach (var kept in new[] { "opportunities", "entries", "awards", "ratings", "Deleted member" })
            Assert.Contains(kept, text, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ helpers

    private static readonly PropertyInfo[] Columns = typeof(User)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true })
        .ToArray();

    /// <summary>
    /// A user with something in every column, so a column the scrub forgets
    /// reads as itself afterwards rather than as an empty default.
    /// </summary>
    private static User Filled()
    {
        var user = new User
        {
            Email = "someone@example.com",
            DisplayName = "Someone Real",
            PasswordHash = "hashed",
            Role = Roles.Freelancer,
        };
        foreach (var column in Columns)
            column.SetValue(user, Sample(Nullable.GetUnderlyingType(column.PropertyType) ?? column.PropertyType));
        return user;
    }

    private static Dictionary<string, object?> Snapshot(User user) =>
        Columns.ToDictionary(c => c.Name, c => c.GetValue(user));

    /// <summary>
    /// Something that is not the type's empty value. A column type this does
    /// not know about is a deliberate failure: nobody should be able to add
    /// one to User without the sweep above seeing it.
    /// </summary>
    private static object Sample(Type type) =>
        type == typeof(string) ? "something the person typed"
        : type == typeof(Guid) ? Guid.NewGuid()
        : type == typeof(bool) ? true
        : type == typeof(int) ? 7
        : type == typeof(long) ? 4_815_162_342L
        : type == typeof(DateTimeOffset) ? Now.AddYears(-1)
        : throw new NotSupportedException($"ErasureTests has no sample value for {type}.");

    /// <summary>The empty value for a column: null, 0, false, or an empty guid.</summary>
    private static object? Blank(Type type) =>
        type == typeof(string) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;

    /// <summary>
    /// The model AppDbContext declares, built and never connected to: the
    /// connection string names a host that does not exist, and nothing here
    /// opens one.
    /// </summary>
    private static AppDbContext Model() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model.invalid;Database=model;Username=none;Password=none")
            .Options);
}
