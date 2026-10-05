using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Storage;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// How work is handed in — repository, upload, or both — and the promises
/// the upload path keeps because the repository path made them first: work
/// lands only while the opportunity is open, freezes at the deadline, claims a
/// milestone once and for good, and is the client's to read from the
/// deadline and not before.
/// </summary>
public class DeliveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    // ------------------------------------------------------------- the choice

    [Theory]
    [InlineData("repository", OpportunityDelivery.Repository)]
    [InlineData("upload", OpportunityDelivery.Upload)]
    [InlineData("both", OpportunityDelivery.Both)]
    [InlineData(" Upload ", OpportunityDelivery.Upload)]
    public void The_three_ways_in_round_trip_by_name(string name, OpportunityDelivery expected)
    {
        Assert.Equal(expected, Delivery.Parse(name));
        Assert.Equal(name.Trim().ToLowerInvariant(), Delivery.Name(expected));
        Assert.Null(Delivery.Problem(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github")]
    [InlineData("files")]
    public void Anything_else_is_refused_with_the_three_named(string? name)
    {
        var problem = Delivery.Problem(name);
        Assert.NotNull(problem);
        Assert.Contains("repository", problem);
        Assert.Contains("upload", problem);
        Assert.Contains("both", problem);
    }

    [Theory]
    [InlineData("repository", true)]
    [InlineData("both", true)]
    [InlineData("upload", false)]
    [InlineData("repository", false)]
    [InlineData("upload", null)]
    [InlineData("files", true)]   // no delivery at all: that problem comes first, this one stays quiet
    public void Docker_compose_can_only_be_required_of_a_repository(string delivery, bool? requiresCompose)
    {
        var problem = Delivery.ComposeProblem(delivery, requiresCompose);
        if (delivery == "upload" && requiresCompose == true)
        {
            Assert.NotNull(problem);
            Assert.Contains("repository", problem);
        }
        else Assert.Null(problem);
    }

    [Fact]
    public void Both_is_the_union_and_each_half_reads_off_it()
    {
        Assert.True(Delivery.UsesRepository(OpportunityDelivery.Both));
        Assert.True(Delivery.UsesUpload(OpportunityDelivery.Both));
        Assert.True(Delivery.UsesRepository(OpportunityDelivery.Repository));
        Assert.False(Delivery.UsesUpload(OpportunityDelivery.Repository));
        Assert.False(Delivery.UsesRepository(OpportunityDelivery.Upload));
        Assert.True(Delivery.UsesUpload(OpportunityDelivery.Upload));
    }

    [Fact]
    public void A_github_username_is_asked_for_exactly_when_a_repository_will_be_made()
    {
        // The username is where the invitation goes; with no repository
        // there is nothing to send and nothing to mistype.
        Assert.True(Delivery.NeedsGithubUsername(OpportunityDelivery.Repository));
        Assert.True(Delivery.NeedsGithubUsername(OpportunityDelivery.Both));
        Assert.False(Delivery.NeedsGithubUsername(OpportunityDelivery.Upload));
    }

    // ---------------------------------------------------------- the clock

    [Fact]
    public void Uploads_land_while_the_opportunity_is_open_and_freeze_at_the_deadline()
    {
        var deadline = Now.AddHours(1);
        Assert.Null(Delivery.UploadProblem(OpportunityStatus.Open, deadline, Now));
        // The deadline itself is the freeze, not one second after.
        Assert.NotNull(Delivery.UploadProblem(OpportunityStatus.Open, deadline, deadline));
        // An opportunity the worker has not yet swept reads the same as one it has.
        Assert.Equal(
            Delivery.UploadProblem(OpportunityStatus.Open, deadline, deadline.AddMinutes(1)),
            Delivery.UploadProblem(OpportunityStatus.Reviewing, deadline, deadline.AddMinutes(1)));
    }

    [Theory]
    [InlineData(OpportunityStatus.Draft)]
    [InlineData(OpportunityStatus.Reviewing)]
    [InlineData(OpportunityStatus.Awarded)]
    [InlineData(OpportunityStatus.Cancelled)]
    public void Nothing_lands_on_an_opportunity_that_is_not_open(OpportunityStatus status) =>
        Assert.NotNull(Delivery.UploadProblem(status, Now.AddDays(1), Now));

    // ------------------------------------------------------------ who sees

    [Fact]
    public void The_entrant_and_an_administrator_always_see_the_files()
    {
        foreach (var status in Enum.GetValues<OpportunityStatus>())
        {
            Assert.True(Delivery.CanSeeFiles(isEntrant: true, isOwner: false, isAdmin: false, status));
            Assert.True(Delivery.CanSeeFiles(isEntrant: false, isOwner: false, isAdmin: true, status));
        }
    }

    [Fact]
    public void The_client_sees_the_files_from_the_deadline_and_not_before()
    {
        // The same promise the repository path makes: an early first draft
        // is not judged before everyone else has handed in.
        Assert.False(Delivery.CanSeeFiles(false, isOwner: true, false, OpportunityStatus.Open));
        Assert.True(Delivery.CanSeeFiles(false, isOwner: true, false, OpportunityStatus.Reviewing));
        Assert.True(Delivery.CanSeeFiles(false, isOwner: true, false, OpportunityStatus.Awarded));
        // A cancelled opportunity delivers nothing — entries are void.
        Assert.False(Delivery.CanSeeFiles(false, isOwner: true, false, OpportunityStatus.Cancelled));
    }

    [Fact]
    public void A_stranger_never_sees_them() =>
        Assert.False(Delivery.CanSeeFiles(false, false, false, OpportunityStatus.Reviewing));

    // ------------------------------------------------------------ deleting

    [Fact]
    public void A_file_that_claimed_a_milestone_stays_and_a_frozen_one_stays_for_the_deadline_reason()
    {
        Assert.Null(Delivery.DeleteProblem(claimedMilestone: false, frozen: null));
        Assert.Contains("claimed a milestone", Delivery.DeleteProblem(claimedMilestone: true, frozen: null));
        // Past the deadline the reason is the deadline, whatever the file did.
        Assert.Equal("frozen", Delivery.DeleteProblem(claimedMilestone: true, frozen: "frozen"));
    }

    // ------------------------------------------------------------- storage

    [Fact]
    public void Submission_keys_carry_the_entry_and_the_file_that_own_them()
    {
        var entry = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var file = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal(
            "entries/11111111111111111111111111111111/files/22222222222222222222222222222222/mark.svg",
            StorageRules.SubmissionKey(entry, file, "mark.svg"));
    }

    [Fact]
    public void An_entry_carries_a_bounded_set_of_files()
    {
        var max = 25L * 1024 * 1024;
        Assert.Null(StorageRules.SubmissionProblem("mark.svg", 1024, max, StorageRules.MaxSubmissionsPerEntry - 1));
        Assert.Contains("at most", StorageRules.SubmissionProblem("mark.svg", 1024, max, StorageRules.MaxSubmissionsPerEntry));
        Assert.Contains("empty", StorageRules.SubmissionProblem("mark.svg", 0, max, 0));
        Assert.Contains("limit", StorageRules.SubmissionProblem("mark.svg", max + 1, max, 0));
    }

    [Theory]
    [InlineData("image/svg+xml", "mark.svg", "image")]
    [InlineData("image/png", "mark.png", "image")]
    [InlineData("image/webp", "mark.webp", "image")]
    [InlineData("application/pdf", "brand-guide.pdf", "pdf")]
    [InlineData("text/markdown", "notes.md", "text")]
    [InlineData("text/html", "notes.html", null)]
    [InlineData(null, "logo-pack.zip", null)]
    public void Handed_in_files_preview_by_the_rule_brief_attachments_use(string? contentType, string fileName, string? kind) =>
        Assert.Equal(kind, StorageRules.Preview(contentType, fileName, 2048)?.Kind);

    // --------------------------------------------------------------- mail

    [Fact]
    public void The_client_is_told_an_application_arrived_and_where_to_answer_it()
    {
        // The client no longer hears of an entry — they make it, by
        // selecting — so the one mail they get is the application itself.
        var mail = Emails.ApplicationReceived("Logo", "logo", "Nadia", 87, 2);
        Assert.Contains("Nadia asks to compete", mail.Subject);
        Assert.Contains("2nd application", mail.TextBody);
        Assert.Contains("87% match", mail.TextBody);
        Assert.DoesNotContain("github.com/", mail.TextBody);
        Assert.Equal("/opportunities/logo", mail.ActionPath);
    }

    [Fact]
    public void Review_notices_send_the_reviewer_to_where_the_work_is()
    {
        var upload = Emails.OpportunityInReviewClient("Logo", "logo", 3, OpportunityDelivery.Upload);
        Assert.DoesNotContain("GitHub", upload.TextBody);
        Assert.Contains("opportunity page", upload.TextBody);

        var repo = Emails.OpportunityInReviewClient("App", "app", 3, OpportunityDelivery.Repository);
        Assert.Contains("GitHub", repo.TextBody);

        var entrant = Emails.OpportunityInReviewEntrant("Logo", "logo", OpportunityDelivery.Upload);
        Assert.DoesNotContain("repository", entrant.TextBody);
        Assert.Contains("uploaded by then", entrant.TextBody);
    }
}
