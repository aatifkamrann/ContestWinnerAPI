using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Email;

/// <summary>What one email says: subject, text paragraphs, an optional call to
/// action whose path is portal-relative until the moment of sending, and — for
/// mail the reader opted into — the path that opts them out again.</summary>
public sealed record EmailContent(
    string Subject, string TextBody, string? ActionText, string? ActionPath, string? UnsubscribePath = null);

/// <summary>
/// The wording of every email the portal sends, as pure functions — the glue
/// that finds recipients lives in <see cref="Notify"/>, and the tests hold
/// this class to the same standard as the help registry: say what happened,
/// what happens next, and what the reader can do about it.
/// </summary>
public static class Emails
{
    public static string Money(decimal amount, string currency) => $"{amount:#,0.##} {currency}";

    /// <summary>1st, 2nd, 3rd, 4th… — for "the Nth entrant".</summary>
    public static string Ordinal(int n)
    {
        var suffix = (n % 100 is 11 or 12 or 13) ? "th" : (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return $"{n}{suffix}";
    }

    // ------------------------------------------------------------ client

    /// <summary>
    /// To the client, in the same save as the application: somebody is
    /// asking to compete, and the review box on the opportunity page is where
    /// to answer them.
    /// </summary>
    public static EmailContent ApplicationReceived(
        string opportunityTitle, string slug, string applicantName, int match, int applicationNumber) => new(
        Subject: $"New application on “{opportunityTitle}” — {applicantName} asks to compete",
        TextBody:
            $"{applicantName} applied to compete in “{opportunityTitle}” — the {Ordinal(applicationNumber)} application, "
            + $"with a {match}% match to the brief.\n\n"
            + "Their application is in the review box on the opportunity page: how they would approach the work, "
            + "the past work they attached, and the hours they commit. Select them to put them in the opportunity — "
            + "a private repository is set up for them the moment you do — or turn the application down. "
            + "Nothing happens until you decide.",
        ActionText: "Review the application",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent OpportunityInReviewClient(
        string opportunityTitle, string slug, int activeEntries, OpportunityDelivery delivery) => new(
        Subject: $"“{opportunityTitle}” has closed — {activeEntries} {(activeEntries == 1 ? "entry is" : "entries are")} waiting on your decision",
        TextBody:
            $"The deadline on “{opportunityTitle}” has passed. "
            + (delivery switch
            {
                OpportunityDelivery.Upload =>
                    "Every entrant's files are frozen as they stood at the deadline, and they are now open to you "
                    + "on the opportunity page.\n\n"
                    + "Review them there, then announce the winner from the same page. ",
                OpportunityDelivery.Both =>
                    "Every repository is being frozen at a final tag with your connected GitHub account granted "
                    + "read access, and every entrant's uploaded files are now open to you on the opportunity page.\n\n"
                    + "Review the code in GitHub and the files on the opportunity page, then announce the winner "
                    + "there. ",
                _ =>
                    "Every repository is being frozen at a final tag and "
                    + "the entrants' push access revoked, and your connected GitHub account is being granted read "
                    + "access to each one.\n\n"
                    + "Review the entries in GitHub's own interface, then announce the winner from the opportunity "
                    + "page. ",
            })
            + "Until you do, every entrant is waiting on a decision they cannot influence — opportunities parked "
            + "in review are where complaints come from.",
        ActionText: "Review the entries",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent TransferRequested(string opportunityTitle, string slug, string repoFullName, string targetLogin) => new(
        Subject: $"Accept the repository transfer for “{opportunityTitle}”",
        TextBody:
            $"The winning repository {repoFullName} has been transferred to @{targetLogin}.\n\n"
            + "GitHub holds a transfer to a personal account until the recipient accepts it — look for "
            + "GitHub's own invitation email. Until you accept, the code you paid for is not yet yours; the "
            + "portal re-reads the repository and marks the handover verified once it sees the new owner.",
        ActionText: "See the opportunity",
        ActionPath: $"/opportunities/{slug}");

    // -------------------------------------------------------- freelancer

    public static EmailContent RepoReady(
        string opportunityTitle, string slug, string repoFullName, string githubUsername, DateTimeOffset? deadlineUtc) => new(
        Subject: $"Your repository for “{opportunityTitle}” is ready",
        TextBody:
            $"Your private opportunity repository is up: https://github.com/{repoFullName}\n\n"
            + $"An invitation went to the GitHub account “{githubUsername}” — accept it there, then clone and "
            + "start. Nobody else in the opportunity can see this repository; the client only gets read access "
            + "after the deadline.\n\n"
            + "Claim milestones from inside the repository: push a tag m1, m2, … (or open a pull request from "
            + "a branch named m1-…) and the board updates within seconds. The brief and the milestone list "
            + "are in the README."
            + (deadlineUtc is { } d ? $"\n\nDeadline: {d:yyyy-MM-dd HH:mm} UTC." : ""),
        ActionText: "Open the opportunity",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent RepoFailed(string opportunityTitle, string slug) => new(
        Subject: $"We could not set up your repository for “{opportunityTitle}”",
        TextBody:
            $"Setting up your private repository for “{opportunityTitle}” failed repeatedly, and the portal has "
            + "stopped retrying. An operator has been notified with the details.\n\n"
            + "Your entry stands — nothing is lost — but you cannot start until this is fixed. If your GitHub "
            + "username was mistyped when you entered, that is the usual cause and worth checking first.",
        ActionText: "Open the opportunity",
        ActionPath: $"/opportunities/{slug}");

    /// <summary>The claim stands; the build of its commit did not. The board has the log.</summary>
    public static EmailContent BuildFailed(string opportunityTitle, string slug, int milestoneNumber, string error) => new(
        Subject: $"Milestone {milestoneNumber} of “{opportunityTitle}” did not build",
        TextBody:
            $"Your claim of milestone m{milestoneNumber} on “{opportunityTitle}” is on the board, but the build of that "
            + "commit failed:\n\n"
            + $"{error}\n\n"
            + "This opportunity requires entries to run with Docker Compose, so each claimed commit is built from the "
            + "compose file at the root of your repository. On the board, the red mark on that milestone opens the "
            + "last lines of the build log, and the whole log can be downloaded from there.\n\n"
            + "The claim stands as it was made. A failed build lowers your standing until a claim builds, so fix the "
            + "build before you claim the next milestone — that one is built afresh.",
        ActionText: "Open the board",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent OpportunityInReviewEntrant(string opportunityTitle, string slug, OpportunityDelivery delivery) => new(
        Subject: $"“{opportunityTitle}” has closed — your work is in review",
        TextBody:
            $"The deadline on “{opportunityTitle}” has passed. "
            + (delivery switch
            {
                OpportunityDelivery.Upload =>
                    "Your files are frozen as they stood at the deadline — what was uploaded by then is what is "
                    + "judged — and the client can now open them for review.\n\n",
                OpportunityDelivery.Both =>
                    "Your repository is frozen at a final tag and your files as they stood at the deadline — "
                    + "what was handed in by then is what is judged — and the client now has both for review.\n\n",
                _ =>
                    "Your repository is frozen at a final tag — what was "
                    + "pushed by the deadline is what is judged — and the client now has read access for review.\n\n",
            })
            + "You will hear from us the moment a winner is announced.",
        ActionText: "See the opportunity",
        ActionPath: $"/opportunities/{slug}");

    /// <summary>To the administrator who pressed the settings test button — proof the pipe works, sent through it.</summary>
    public static EmailContent TestSend(string portalName) => new(
        Subject: $"Test email from {portalName}",
        TextBody:
            "You asked the settings screen to prove the email pipeline, and this is the proof: it left "
            + "through the same email setup, sender address, and template every notification uses.\n\n"
            + "If this landed in spam, real notifications will too — the fix (SPF, DKIM, a sender with some "
            + "history) belongs to the domain, not the portal.",
        ActionText: "Back to settings",
        ActionPath: "/admin/settings");

    public static EmailContent OpportunityCancelled(string opportunityTitle, string slug, string reason) => new(
        Subject: $"“{opportunityTitle}” was cancelled by the client",
        TextBody:
            $"The client called off “{opportunityTitle}” before choosing a winner. Their reason:\n\n"
            + $"“{reason}”\n\n"
            + "Your repository is being archived — read-only from here, never transferred and never deleted, "
            + "so everything you built stays yours and stays private. Clone anything you want to keep working on.\n\n"
            + "The cancellation goes on this client's public record, next to their payment history — the next "
            + "entrants will see it before they stake their time.",
        ActionText: "See the notice",
        ActionPath: $"/opportunities/{slug}");

    /// <summary>
    /// To the entrant taken off an opportunity. The one email on this portal that
    /// tells somebody their staked work no longer counts, so it carries the
    /// client's reason unedited, says exactly what happens to the code, and
    /// gives the deadline for getting it out.
    /// </summary>
    public static EmailContent EntryRemoved(
        string opportunityTitle, string slug, string reason, int graceDays,
        string? repoFullName, bool byAdmin) => new(
        Subject: $"Your entry in “{opportunityTitle}” has been removed",
        TextBody:
            (byAdmin
                ? $"A portal administrator removed your entry from “{opportunityTitle}”. The reason given:\n\n"
                : $"The client removed your entry from “{opportunityTitle}”. Their reason:\n\n")
            + $"“{reason}”\n\n"
            + (repoFullName is null
                ? "No repository had been created for your entry yet, so there is nothing to collect.\n\n"
                : $"Your repository {repoFullName} is being archived now — read-only, so nothing more can be "
                  + $"pushed to it. You keep read access for {graceDays} days from today: clone it before "
                  + "then, because after that your access to it ends. It is never transferred to the client "
                  + "and never deleted.\n\n")
            + "You cannot re-enter this opportunity. Every other opportunity on the portal is unaffected — this is "
            + (byAdmin ? "one decision about one entry" : "one client's decision about one entry")
            + ", and it does not appear on your public record.\n\n"
            // No invitation to reply: the portal sends from a no-reply
            // address by default and has no inbound channel to promise.
            + "Every removal is recorded with who made it, when, and this reason, so a portal "
            + "administrator can look at it if you believe it was wrong.",
        ActionText: "Your entries",
        ActionPath: "/entries");

    /// <summary>To the client, when an administrator removed somebody from their opportunity.</summary>
    public static EmailContent EntrantRemovedByAdmin(
        string opportunityTitle, string slug, string entrantName, string reason) => new(
        Subject: $"An administrator removed an entrant from “{opportunityTitle}”",
        TextBody:
            $"{entrantName} has been removed from your opportunity “{opportunityTitle}” by a portal "
            + $"administrator. The reason given:\n\n“{reason}”\n\n"
            + "Your entrant list and board no longer show them, and their repository is being archived. "
            + "Nobody else's entry is affected.",
        ActionText: "See the opportunity",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent AwardWon(string opportunityTitle, string slug, decimal amount, string currency) => new(
        Subject: $"You won “{opportunityTitle}”",
        TextBody:
            $"Your entry won “{opportunityTitle}” — the award is {Money(amount, currency)}.\n\n"
            + "What happens next: the client pays you directly (the portal holds no funds) and confirms the "
            + "payment here. That confirmation is what starts the transfer of your repository to them — "
            + "nothing changes hands before you are paid.\n\n"
            + "The client reads where to send it from Payout Setup on your profile, which they can see now "
            + "that they have announced you — if it is empty, fill it in today.\n\n"
            + "The payment shows on this opportunity's public record either way, so an award left unpaid is "
            + "visible to everyone who looks.",
        ActionText: "See the award",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent AwardLost(string opportunityTitle) => new(
        Subject: $"“{opportunityTitle}” went to another entry",
        TextBody:
            $"The client on “{opportunityTitle}” chose a different winner. Your repository has been archived, not "
            + "transferred — losing work never changes hands, so what you built stays yours and stays "
            + "private.\n\n"
            + "Every finished entry builds your public track record here: milestones claimed and code pushed "
            + "are visible on your profile whether or not you win.",
        ActionText: "Find your next opportunity",
        ActionPath: "/");

    public static EmailContent AwardPaid(string opportunityTitle, string slug, decimal amount, string currency, string? handoverNote) => new(
        Subject: $"Payment on “{opportunityTitle}” is confirmed",
        TextBody:
            $"The client confirmed paying {Money(amount, currency)} for “{opportunityTitle}”.\n\n"
            + (handoverNote is null
                ? "The transfer of your repository to them has been requested — this closes the opportunity out. "
                  + "If the payment has not actually reached you, say so now: the paper trail is freshest today."
                : $"No repository transfer was started: {handoverNote} If the payment has not actually reached "
                  + "you, say so now: the paper trail is freshest today.")
            + "\n\n"
            + "Once the money lands, rate working with this client on the opportunity page. Your rating and the "
            + "payment record are what the next entrants read before they stake their time.",
        ActionText: "See the award",
        ActionPath: $"/opportunities/{slug}");

    /// <summary>To whichever party was scored. Deduped per award and rater, so an edited score never mails twice.</summary>
    public static EmailContent RatingReceived(string opportunityTitle, string slug, int stars, string byName) => new(
        Subject: $"{byName} rated you {stars} star{(stars == 1 ? "" : "s")} on “{opportunityTitle}”",
        TextBody:
            $"{byName} rated working with you on “{opportunityTitle}”: {new string('★', stars)}{new string('☆', 5 - stars)}.\n\n"
            + "Ratings are public and follow your account — they show beside your name wherever the other side "
            + "is deciding whether to work with you. If there is a comment, it is on the opportunity page.",
        ActionText: "See the rating",
        ActionPath: $"/opportunities/{slug}");

    // -------------------------------------------------------- broadcasts
    // To everyone who asked, under Notifications, to hear about these. Each
    // says why it arrived; the fan-out adds the one-click opt-out, since the
    // recipient is unknown here.

    public static EmailContent OpportunityOpened(
        string opportunityTitle, string slug, string clientName, decimal amount, string currency,
        DateTimeOffset? deadlineUtc, DateTimeOffset? startsAtUtc = null) => new(
        Subject: $"New opportunity: “{opportunityTitle}” — {Money(amount, currency)} fixed award",
        TextBody:
            $"{clientName} just opened “{opportunityTitle}” with a fixed award of {Money(amount, currency)}."
            // Entry is open from publishing; a later start day is when the
            // work is meant to begin, and the reader plans their weeks by it.
            + (startsAtUtc is { } s ? $" Entry is open now, and the work starts {s:yyyy-MM-dd} UTC." : "")
            + (deadlineUtc is { } d ? $" Entry closes {d:yyyy-MM-dd HH:mm} UTC." : "")
            + "\n\n"
            + "Read the brief and, if it is your kind of work, enter early: a private repository is yours the "
            + "moment you enter, and every milestone you claim by pushing code shows on the board from the "
            + "first commit. Only the winner is paid, and the client reviews the code before anyone is.\n\n"
            + "You asked to hear the moment an opportunity opens.",
        ActionText: "Read the brief",
        ActionPath: $"/opportunities/{slug}");

    public static EmailContent WinnerAnnounced(
        string opportunityTitle, string slug, string winnerName, decimal amount, string currency) => new(
        Subject: $"Winner announced: “{opportunityTitle}” went to {winnerName}",
        TextBody:
            $"The client on “{opportunityTitle}” chose {winnerName}'s entry for the {Money(amount, currency)} award.\n\n"
            + "What happens next is public: the client pays the winner directly and confirms it on the opportunity "
            + "page, where the payment record — and both sides' ratings once it is paid — is there for the "
            + "next entrants to read before they stake their time.\n\n"
            + "You asked to hear when a winner is announced.",
        ActionText: "See the opportunity",
        ActionPath: $"/opportunities/{slug}");

    // ----------------------------------------------------------- account

    /// <summary>
    /// To whoever asked — or to whoever's address a stranger typed into the
    /// forgot form, which is why the wording assumes nothing about the reader.
    /// </summary>
    public static EmailContent PasswordReset(string token) => new(
        Subject: "Reset your password",
        TextBody:
            "Someone — hopefully you — asked to reset the password on this account. The link below "
            + "chooses a new one; it works once, and for the next hour.\n\n"
            + "If you did not ask, ignore this: nothing has changed, your current password still works, "
            + "and whoever typed your address learned nothing from it.",
        ActionText: "Choose a new password",
        ActionPath: $"/reset-password?token={token}");

    /// <summary>
    /// After a signed-in member changed their own password. Mostly it tells
    /// them what they already know; it exists for the day it was not them,
    /// so it says how to take the account back.
    /// </summary>
    public static EmailContent PasswordChanged() => new(
        Subject: "Your password was changed",
        TextBody:
            "The password on this account was just changed from its settings page, and every other device "
            + "signed into it was signed out.\n\n"
            + "If that was you, there is nothing to do. If it was not, choose a new password from the link "
            + "below straight away: it signs out whoever changed it.",
        ActionText: "Reset the password",
        ActionPath: "/forgot-password");

    /// <summary>
    /// An account somebody else made. Unlike the reset above, this reader
    /// did not ask for anything, so the mail has to say who made it and
    /// what it is for before it asks them to click — and it has to say what
    /// the account can do, because the type was chosen for them and is the
    /// one thing about it that never changes.
    /// </summary>
    public static EmailContent AccountInvitation(string token, string role, int days) => new(
        Subject: "An account has been created for you",
        TextBody:
            "A portal administrator created an account for you and set no password on it — the link below "
            + $"chooses your first one. It works once, and for the next {days} days.\n\n"
            + RoleLine(role) + "\n\n"
            + "If you were not expecting this, you can ignore it. The account cannot be signed into until "
            + "somebody sets a password through this link, and the link is only in this email.",
        ActionText: "Choose your password",
        ActionPath: $"/reset-password?token={token}");

    /// <summary>
    /// The first email an account gets, queued in the same save that makes
    /// it. Nobody asked for it, so it earns its place by doing two things
    /// the register form did not: say how an opportunity here actually runs, in
    /// the reader's own terms, and name the one thing worth doing next —
    /// which differs by role, and is the reason there are two.
    /// </summary>
    /// <summary>
    /// The code that lets a new account in. The subject carries it, so a
    /// phone's notification shade is enough — nobody should have to open
    /// the message to type six digits. No button: there is nothing to click,
    /// and a link that signed the reader in would be a code that leaked by
    /// forwarding.
    /// </summary>
    // ------------------------------------------------------ identity

    /// <summary>
    /// The provider approved them. Which door they were at is what the
    /// next step names: a client came to publish, a freelancer to apply.
    /// </summary>
    public static EmailContent IdentityVerified(string portalName, string role) => role == Roles.Client
        ? new(
            Subject: $"Your identity is verified on {portalName}",
            TextBody:
                $"The verification you started on {portalName} has passed. You will not be asked again.\n\n"
                + "An opportunity you publish now carries that behind it: entrants who stake a fortnight on your "
                + "brief know a real person made the promise.",
            ActionText: "Your opportunities",
            ActionPath: "/client/opportunities")
        : new(
            Subject: $"Your identity is verified on {portalName}",
            TextBody:
                $"The verification you started on {portalName} has passed. You will not be asked again.\n\n"
                + "Your profile now carries the verified mark, applications are open to you, and a client "
                + "who chooses you can read how to pay you.",
            ActionText: "Browse open opportunities",
            ActionPath: "/opportunities");

    /// <summary>
    /// The provider declined them. The reason is the provider's category —
    /// a document that would not read, a face that did not match — and the
    /// portal's part is to say it can be tried again.
    /// </summary>
    public static EmailContent IdentityDeclined(string portalName, string? reason) => new(
        Subject: $"Your verification on {portalName} did not pass",
        TextBody:
            $"The identity verification you started on {portalName} was declined"
            + (reason is null ? "." : $": {reason}.")
            + "\n\nThat is usually a photograph the provider could not read — glare, a cropped edge, an "
            + "expired document — rather than anything about you. You can start again straight away, with "
            + "the same or another document; the doors that asked for verification stay shut until a "
            + "verification passes, and nothing else on your account is affected.",
        ActionText: "Try again",
        ActionPath: "/verify");

    public static EmailContent ConfirmationCode(string portalName, string code) => new(
        Subject: $"{code} is your {portalName} code",
        TextBody:
            $"Enter this code on {portalName} to confirm your email address:\n\n"
            + $"{code}\n\n"
            + $"It works for {(int)Auth.Confirmation.Lifetime.TotalMinutes} minutes. If you did not create an "
            + $"account on {portalName}, ignore this — nobody can use your address without the code, and the "
            + "account it belongs to can do nothing until somebody enters it.",
        ActionText: null,
        ActionPath: null);

    public static EmailContent Welcome(string portalName, string role) => role == Roles.Client
        ? new(
            Subject: $"Welcome to {portalName}",
            TextBody:
                $"Your client account on {portalName} is ready.\n\n"
                + "How it works: you write a brief, name one fixed award, set a deadline, and publish. Anyone "
                + "may enter. Each entrant builds in a private repository of their own — or hands files in on "
                + "the opportunity page — and a progress board shows what every one of them has actually done. At "
                + "the deadline everything freezes: you read the work, choose a winner, pay them directly, and "
                + "the winning repository transfers to you.\n\n"
                + "Nothing is paid up front and the portal holds no money — the award goes from you to the "
                + "winner, and the payment record beside your name is what the next entrants read before they "
                + "stake their time.\n\n"
                + "The first thing to do is draft a brief. The milestones you list become each entrant's "
                + "board, so the checklist you write is what progress gets measured against.",
            ActionText: "Draft a brief",
            ActionPath: "/client/opportunities/new")
        : new(
            Subject: $"Welcome to {portalName}",
            TextBody:
                $"Your freelancer account on {portalName} is ready.\n\n"
                + "How it works: clients post briefs with a fixed award and a deadline, and entry is free and "
                + "open. Each entrant works in a private repository of their own — nobody else in the opportunity "
                + "can see it — and claims milestones by pushing code, which the progress board shows the "
                + "moment it lands. At the deadline the client reads the code and picks one winner, who is "
                + "paid directly. Work that did not win stays its author's.\n\n"
                + "Before you enter anything, write your profile: it is what a client reads before they open "
                + "your code, and it earns the merit score shown beside your name on every entrant list. Then "
                + "browse the open opportunities. A repository opportunity asks for your GitHub username when you "
                + "enter — the invitation to your private repository goes there.",
            ActionText: "Write your profile",
            ActionPath: "/profile");

    /// <summary>
    /// To the applicant, in the same save as the application: it is filed,
    /// and what happens to it next.
    /// </summary>
    public static EmailContent ApplicationSubmitted(string opportunityTitle, string slug, string clientName) => new(
        Subject: $"Your application on “{opportunityTitle}” is under review",
        TextBody:
            $"Your application to compete in “{opportunityTitle}” by {clientName} is filed.\n\n"
            + "The client reads it — your approach, the past work you attached and the hours you commit — "
            + "and selects who competes. You are told either way, and nothing is expected of you until then. "
            + "Your application page shows where it stands.",
        ActionText: "See your application",
        ActionPath: $"/opportunities/{slug}/apply");

    /// <summary>
    /// To the applicant, in the same save as the entry a selection made.
    /// The terms they are now committed to, in one place they can find
    /// again when the page is not in front of them: the award, the dates,
    /// how work is handed in, and the checklist the board measures them
    /// against. The repository, where there is one, has its own email for
    /// when it exists; this one only says it is coming.
    /// </summary>
    public static EmailContent ApplicationSelected(
        string opportunityTitle, string slug, string clientName, decimal amount, string currency,
        OpportunityDelivery delivery, DateTimeOffset? deadlineUtc, DateTimeOffset? entryCloseUtc,
        IReadOnlyList<(string Title, DateTimeOffset? DueUtc)> milestones, int entrantNumber,
        string githubUsername) => new(
        Subject: $"You are selected to compete in “{opportunityTitle}” — {Money(amount, currency)} fixed award",
        TextBody:
            $"{clientName} selected your application: you are in “{opportunityTitle}”, and you are the "
            + $"{Ordinal(entrantNumber)} entrant.\n\n"
            + JoinedTerms(amount, currency, delivery, deadlineUtc, entryCloseUtc, milestones, githubUsername),
        ActionText: "Open the opportunity",
        ActionPath: $"/opportunities/{slug}");

    /// <summary>To the applicant: the answer, and that it is not a mark against them.</summary>
    public static EmailContent ApplicationNotSelected(string opportunityTitle, string slug) => new(
        Subject: $"“{opportunityTitle}” — your application was not selected",
        TextBody:
            $"The client on “{opportunityTitle}” did not select your application this time.\n\n"
            + "Nothing about it is held against you: an application is read once, for one brief, and the "
            + "client chooses a field, not a verdict on anybody. Your profile and your record here are "
            + "unchanged, and every open opportunity is still yours to apply to.",
        ActionText: "Find your next opportunity",
        ActionPath: "/");

    /// <summary>
    /// To the applicant, when a selection is taken back before any work
    /// arrived in the entry it made. Not a removal: no reason is owed, and
    /// they may still be selected while the opportunity is open.
    /// </summary>
    public static EmailContent SelectionTakenBack(string opportunityTitle, string slug, bool hadRepository) => new(
        Subject: $"“{opportunityTitle}” — your selection was taken back",
        TextBody:
            $"The client on “{opportunityTitle}” has taken back their selection of your application, so you are no "
            + "longer an entrant in it."
            + (hadRepository ? " The private repository made for your entry has been archived." : "")
            + "\n\nIt happened before any work arrived, and nothing about it is held against you: your profile "
            + "and your record here are unchanged. The client may still select you while the opportunity is open, "
            + "and every other open opportunity is yours to apply to.",
        ActionText: "Find your next opportunity",
        ActionPath: "/");

    /// <summary>To the client, when an administrator decided over them: their field changed and they did not do it.</summary>
    public static EmailContent ApplicationDecidedByAdmin(
        string opportunityTitle, string slug, string applicantName, bool selected, bool takenBack = false) => new(
        Subject: selected
            ? $"An administrator selected {applicantName} for “{opportunityTitle}”"
            : takenBack
                ? $"An administrator took back {applicantName}’s selection on “{opportunityTitle}”"
                : $"An administrator turned down {applicantName}’s application on “{opportunityTitle}”",
        TextBody:
            (selected
                ? $"A portal administrator selected {applicantName}’s application on “{opportunityTitle}”: they are "
                  + "now an entrant, and a private repository is being set up for them where the opportunity uses one."
                : takenBack
                    ? $"A portal administrator took back {applicantName}’s selection on “{opportunityTitle}”: they are no "
                      + "longer an entrant, their entry is off the board, and any repository made for it is archived. "
                      + "They have been told."
                    : $"A portal administrator turned down {applicantName}’s application on “{opportunityTitle}”. "
                      + "They have been told, and nothing else changes.")
            + "\n\nThe review box on the opportunity page shows every application and what became of it.",
        ActionText: "See the opportunity",
        ActionPath: $"/opportunities/{slug}");

    /// <summary>
    /// To an applicant nobody answered before the opportunity closed — at its
    /// deadline, or cancelled by the client. Nothing more happens to the
    /// application, and it is not a no, so the email does not call it one.
    /// </summary>
    public static EmailContent ApplicationClosedUndecided(string opportunityTitle, bool cancelled) => new(
        Subject: $"“{opportunityTitle}” closed before your application was decided",
        TextBody:
            (cancelled
                ? $"The client cancelled “{opportunityTitle}” before deciding on your application"
                : $"“{opportunityTitle}” reached its deadline before the client decided on your application")
            + ", so nothing more happens to it.\n\n"
            + "It was not turned down, and nothing about it is held against you: your profile and your record "
            + "here are unchanged, and every open opportunity is yours to apply to.",
        ActionText: "Find your next opportunity",
        ActionPath: "/");

    /// <summary>
    /// The terms an entrant is committed to, as one email body: the award,
    /// the dates, how work is handed in, and the milestone checklist.
    /// </summary>
    private static string JoinedTerms(
        decimal amount, string currency, OpportunityDelivery delivery, DateTimeOffset? deadlineUtc,
        DateTimeOffset? entryCloseUtc, IReadOnlyList<(string Title, DateTimeOffset? DueUtc)> milestones,
        string githubUsername)
    {
        var repo = delivery.HasFlag(OpportunityDelivery.Repository);
        var upload = delivery.HasFlag(OpportunityDelivery.Upload);
        var sb = new System.Text.StringBuilder();

        sb.Append($"The award is {Money(amount, currency)}, fixed, paid by the client directly to the one "
                  + "winner. Only the winner is paid, and the client reads the work before anyone is.\n\n");

        if (deadlineUtc is { } d)
        {
            sb.Append($"Deadline: {d:yyyy-MM-dd HH:mm} UTC. The build ends and the board freezes then — what "
                      + "is handed in by the deadline is what is judged.");
            // Only where the two dates differ; a newcomer's cut-off says
            // nothing to somebody already in.
            if (entryCloseUtc is { } e && e < d)
                sb.Append($" Entry closes to newcomers {e:yyyy-MM-dd HH:mm} UTC; you are already in.");
            sb.Append("\n\n");
        }

        if (repo)
            sb.Append("A private repository is being set up for you now, and the invitation goes to the "
                      + $"GitHub account “{githubUsername}” — a separate email tells you when it is ready. "
                      + "Claim milestones from inside it by pushing a tag m1, m2, … (or opening a pull "
                      + "request from a branch named m1-…) and the board updates within seconds.");
        if (repo && upload) sb.Append(' ');
        if (upload)
            sb.Append("Hand your work in as files on the opportunity page — nobody else entering sees them, "
                      + "and the client sees them when the deadline passes. Tag an upload with a "
                      + "milestone to claim it.");
        if (repo || upload) sb.Append("\n\n");

        if (milestones.Count > 0)
        {
            sb.Append("Milestones — the checklist your board is measured against:\n\n");
            for (var i = 0; i < milestones.Count; i++)
            {
                var (title, due) = milestones[i];
                sb.Append(i + 1).Append(". ").Append(title);
                if (due is { } m) sb.Append($" — due by {m:yyyy-MM-dd HH:mm} UTC");
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append("You may withdraw any time before the opportunity goes to review; withdrawing frees your "
                  + "slot, and a repository is archived, never deleted.");
        return sb.ToString();
    }

    /// <summary>What the account can do, in the reader's own terms rather than the role word.</summary>
    private static string RoleLine(string role) => role switch
    {
        Roles.Client =>
            "It is a client account: you post briefs with a fixed award, read the code entrants build, "
            + "and pay the one you choose.",
        Roles.Admin =>
            "It is an administrator account: it can see and change every opportunity, account and setting on "
            + "this portal. Choose a password you use nowhere else.",
        _ =>
            "It is a freelancer account: you enter opportunities, build in a private repository of your own, "
            + "and are paid if the client picks your entry.",
    };

    // ------------------------------------------------------------- admin

    public static EmailContent RepoFailedAdmin(
        string opportunityTitle, string githubUsername, int attempts, string? note) => new(
        Subject: $"Repo provisioning failed: “{opportunityTitle}” / {githubUsername}",
        TextBody:
            $"Provisioning the repository for {githubUsername} on “{opportunityTitle}” failed permanently after "
            + $"{attempts} attempts.\n\n"
            + $"Last error: {note ?? "no error recorded"}\n\n"
            + "This entrant cannot begin while the opportunity clock runs, and it does not resolve on its own once "
            + "the worker has given up. The usual causes: the GitHub App lost access to the organisation, the "
            + "installation was revoked, or the entrant's username does not exist.",
        ActionText: "Open the dashboard",
        ActionPath: "/dashboard");

    // ------------------------------------------------------------ digest

    /// <summary>One line per open item, assembled by the worker per user per day.</summary>
    public static EmailContent Digest(string portalName, IReadOnlyList<string> lines) => new(
        Subject: $"{portalName} digest — {lines.Count} {(lines.Count == 1 ? "item needs" : "items need")} you",
        TextBody:
            "Still open as of this morning:\n\n"
            + string.Join("\n\n", lines.Select(l => "• " + l)),
        ActionText: "Open your dashboard",
        ActionPath: "/dashboard");
}
