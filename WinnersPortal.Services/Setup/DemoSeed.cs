using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Setup;

/// <summary>
/// Demo data for the SETUP_AUTO path, so `docker compose up` lands on a
/// populated feed instead of an empty portal — the demo is the deliverable.
/// Runs once: it refuses to touch a database that already has opportunities.
/// </summary>
public static class DemoSeed
{
    public const string Password = "winners-demo";

    private static readonly PasswordHasher<User> Hasher = new();

    public static async Task<bool> RunAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Opportunities.AnyAsync(ct)) return false;

        var now = DateTimeOffset.UtcNow;

        var client = NewUser("client@winnersportal.local", "Ayesha Karim", Roles.Client, now);
        var freelancer = NewUser("freelancer@winnersportal.local", "Bilal Hossain", Roles.Freelancer, now);
        var entrant2 = NewUser("nadia@winnersportal.local", "Nadia Rahman", Roles.Freelancer, now);
        var entrant3 = NewUser("tariq@winnersportal.local", "Tariq Mehmood", Roles.Freelancer, now);
        db.Users.AddRange(client, freelancer, entrant2, entrant3);

        var dashboard = new Opportunity
        {
            Id = Guid.NewGuid(),
            Slug = "inventory-billing-dashboard-textile-wholesaler",
            Title = "Inventory & billing dashboard for a textile wholesaler",
            BriefMarkdown =
                "## What we need\n\n" +
                "We run a fabric wholesale business with three warehouses and about 400 SKUs. " +
                "Stock lives in spreadsheets today and billing is manual. We want a small web dashboard that our two clerks can actually use.\n\n" +
                "## Scope\n\n" +
                "- Stock in/out entry with a running balance per SKU and warehouse\n" +
                "- Invoice creation with our GST fields and a printable PDF\n" +
                "- A low-stock report the owner checks every morning\n\n" +
                "## Definition of done\n\n" +
                "Runs with `docker compose up`, seeded with our sample SKU sheet (attached in the repo), and a clerk can raise an invoice in under a minute.\n\n" +
                "Stack is your choice; we can host anything that runs in Docker. Urdu labels are a plus, not a requirement.",
            AwardAmount = 750,
            Status = OpportunityStatus.Open,
            DeadlineUtc = now.AddDays(10),
            ClientId = client.Id,
            CreatedAtUtc = now.AddDays(-4),
            PublishedAtUtc = now.AddDays(-4),
            Milestones =
            [
                new Milestone { Id = Guid.NewGuid(), Order = 0, Title = "Data model & seed import", Description = "SKUs, warehouses, opening balances loaded from the sample sheet." },
                new Milestone { Id = Guid.NewGuid(), Order = 1, Title = "Stock in/out entry", Description = "Running balance per SKU per warehouse; no negative stock." },
                new Milestone { Id = Guid.NewGuid(), Order = 2, Title = "Invoicing with GST fields", Description = "Create, edit, void; totals match our sample invoices." },
                new Milestone { Id = Guid.NewGuid(), Order = 3, Title = "Printable invoice PDF", Description = "A4, one page for up to 20 lines." },
                new Milestone { Id = Guid.NewGuid(), Order = 4, Title = "Low-stock report & compose file", Description = "Threshold per SKU; `docker compose up` brings up everything." },
            ],
        };

        var whatsapp = new Opportunity
        {
            Id = Guid.NewGuid(),
            Slug = "whatsapp-order-notification-bridge",
            Title = "WhatsApp order-notification bridge for a grocery chain",
            BriefMarkdown =
                "## Background\n\n" +
                "Our five stores take phone orders that staff type into a Google Form. Customers keep calling to ask where their order is.\n\n" +
                "## What to build\n\n" +
                "A small service that watches the order sheet and sends WhatsApp status messages (received → packed → out for delivery) via the WhatsApp Business Cloud API. " +
                "Include a one-page admin screen to see today's orders and resend a failed message.\n\n" +
                "## Notes\n\n" +
                "We already have a Meta business account and a test number. Message templates must be configurable — do not hard-code the text.",
            AwardAmount = 400,
            Status = OpportunityStatus.Open,
            DeadlineUtc = now.AddDays(7),
            ClientId = client.Id,
            CreatedAtUtc = now.AddDays(-2),
            PublishedAtUtc = now.AddDays(-2),
            Milestones =
            [
                new Milestone { Id = Guid.NewGuid(), Order = 0, Title = "Sheet polling & order state", Description = "Detect new and changed rows reliably; idempotent." },
                new Milestone { Id = Guid.NewGuid(), Order = 1, Title = "WhatsApp Cloud API integration", Description = "Template messages on each status change; retries with backoff." },
                new Milestone { Id = Guid.NewGuid(), Order = 2, Title = "Admin screen", Description = "Today's orders, delivery status of each message, manual resend." },
                new Milestone { Id = Guid.NewGuid(), Order = 3, Title = "Config & deploy", Description = "Templates and credentials via env; compose file; README." },
            ],
        };

        var draft = new Opportunity
        {
            Id = Guid.NewGuid(),
            Slug = "customer-feedback-portal",
            Title = "Customer feedback portal with QR codes per store",
            BriefMarkdown = "Rough idea: a QR code on each counter opens a 30-second feedback form; owners see a weekly summary. Still writing the details.",
            AwardAmount = 300,
            Status = OpportunityStatus.Draft,
            ClientId = client.Id,
            CreatedAtUtc = now.AddHours(-6),
            Milestones =
            [
                new Milestone { Id = Guid.NewGuid(), Order = 0, Title = "Feedback form & QR generator" },
            ],
        };

        // An opportunity already in review, so the demo client sees the announce
        // flow and the board with a finished run on it.
        var locator = new Opportunity
        {
            Id = Guid.NewGuid(),
            Slug = "store-locator-widget",
            Title = "Store locator widget for a pharmacy chain",
            BriefMarkdown =
                "## What we need\n\n" +
                "An embeddable map widget for our website: 23 branches, search by area, opening hours, " +
                "and a 'directions' link. Must load fast on cheap phones.\n\n" +
                "Plain JS embed — one script tag — and an admin JSON file for the branch list.",
            AwardAmount = 250,
            Status = OpportunityStatus.Reviewing,
            DeadlineUtc = now.AddDays(-1),
            ClientId = client.Id,
            CreatedAtUtc = now.AddDays(-9),
            PublishedAtUtc = now.AddDays(-9),
            Milestones =
            [
                new Milestone { Id = Guid.NewGuid(), Order = 0, Title = "Map & branch data", Description = "All 23 branches from the JSON file, clustered sensibly." },
                new Milestone { Id = Guid.NewGuid(), Order = 1, Title = "Search & hours", Description = "Area search; today's hours with open/closed state." },
                new Milestone { Id = Guid.NewGuid(), Order = 2, Title = "Embed & docs", Description = "One script tag, under 60 KB gzipped, README with options." },
            ],
        };

        db.Opportunities.AddRange(dashboard, whatsapp, draft, locator);

        // The repo fields and checkpoints below are what the GitHub worker and
        // webhooks would have written; seeded so the board demos without a
        // real GitHub App. Provisioned status also keeps the worker's hands
        // off these fake repo names if an app is configured later.
        var bilalDash = new Entry
        {
            Id = Guid.NewGuid(), OpportunityId = dashboard.Id, FreelancerId = freelancer.Id,
            GithubUsername = "bilal-builds", Note = "Full-stack .NET + React; built two POS systems for wholesalers.",
            CreatedAtUtc = now.AddDays(-3),
            ProvisionStatus = RepoProvisionStatus.Provisioned,
            RepoFullName = "astrik-opportunities/inventory-billing-dashboard-textile-wholesaler-bilal-builds",
            DefaultBranch = "main", LastPushAtUtc = now.AddHours(-3), PushCount = 24,
        };
        var nadiaDash = new Entry
        {
            Id = Guid.NewGuid(), OpportunityId = dashboard.Id, FreelancerId = entrant2.Id,
            GithubUsername = "nadia-codes", Note = "Django specialist, strong on reporting and PDF generation.",
            CreatedAtUtc = now.AddDays(-3).AddHours(5),
            ProvisionStatus = RepoProvisionStatus.Provisioned,
            RepoFullName = "astrik-opportunities/inventory-billing-dashboard-textile-wholesaler-nadia-codes",
            DefaultBranch = "main", LastPushAtUtc = now.AddHours(-26), PushCount = 15,
        };
        var tariqDash = new Entry
        {
            Id = Guid.NewGuid(), OpportunityId = dashboard.Id, FreelancerId = entrant3.Id,
            GithubUsername = "tariq-dev", Note = null,
            CreatedAtUtc = now.AddDays(-1),
            ProvisionStatus = RepoProvisionStatus.Provisioned,
            RepoFullName = "astrik-opportunities/inventory-billing-dashboard-textile-wholesaler-tariq-dev",
            DefaultBranch = "main",
        };
        var nadiaWhatsapp = new Entry
        {
            Id = Guid.NewGuid(), OpportunityId = whatsapp.Id, FreelancerId = entrant2.Id,
            GithubUsername = "nadia-codes", Note = "Shipped a WhatsApp Cloud API bot this spring.",
            CreatedAtUtc = now.AddDays(-1).AddHours(2),
            ProvisionStatus = RepoProvisionStatus.Provisioned,
            RepoFullName = "astrik-opportunities/whatsapp-order-notification-bridge-nadia-codes",
            DefaultBranch = "main", LastPushAtUtc = now.AddHours(-8), PushCount = 6,
        };
        var bilalLocator = new Entry
        {
            Id = Guid.NewGuid(), OpportunityId = locator.Id, FreelancerId = freelancer.Id,
            GithubUsername = "bilal-builds", Note = "Leaflet + clustering; done this for a bank.",
            CreatedAtUtc = now.AddDays(-8),
            ProvisionStatus = RepoProvisionStatus.Provisioned,
            RepoFullName = "astrik-opportunities/store-locator-widget-bilal-builds",
            DefaultBranch = "main", LastPushAtUtc = now.AddDays(-1).AddHours(-2), PushCount = 31,
            FrozenAtUtc = now.AddDays(-1),
        };
        var tariqLocator = new Entry
        {
            Id = Guid.NewGuid(), OpportunityId = locator.Id, FreelancerId = entrant3.Id,
            GithubUsername = "tariq-dev", Note = null,
            CreatedAtUtc = now.AddDays(-6),
            ProvisionStatus = RepoProvisionStatus.Provisioned,
            RepoFullName = "astrik-opportunities/store-locator-widget-tariq-dev",
            DefaultBranch = "main", LastPushAtUtc = now.AddDays(-2), PushCount = 9,
            FrozenAtUtc = now.AddDays(-1),
        };
        db.Entries.AddRange(bilalDash, nadiaDash, tariqDash, nadiaWhatsapp, bilalLocator, tariqLocator);

        db.Checkpoints.AddRange(
            Claim(bilalDash, dashboard, 1, now.AddDays(-2), "9f2c41a"),
            Claim(bilalDash, dashboard, 2, now.AddHours(-3), "d81e07b"),
            Claim(nadiaDash, dashboard, 1, now.AddDays(-2).AddHours(7), "4b93cd0"),
            Claim(nadiaWhatsapp, whatsapp, 1, now.AddHours(-8), "77aa914"),
            Claim(bilalLocator, locator, 1, now.AddDays(-6), "0c5e112"),
            Claim(bilalLocator, locator, 2, now.AddDays(-3), "e4197f8"),
            Claim(bilalLocator, locator, 3, now.AddDays(-1).AddHours(-2), "b622a90"),
            Claim(tariqLocator, locator, 1, now.AddDays(-4), "51d0b3c"));

        await db.SaveChangesAsync(ct);

        // Feed counters, through the same helper every production write uses
        // — hand-set numbers here would be a second source of truth waiting
        // to drift from the rows above.
        foreach (var opportunity in new[] { dashboard, whatsapp, draft, locator })
            await Recount.OpportunityAsync(db, opportunity.Id, ct);

        return true;
    }

    /// <summary>A checkpoint as the webhook would have stamped it — number is the 1-based tag.</summary>
    private static Checkpoint Claim(Entry entry, Opportunity opportunity, int number, DateTimeOffset at, string sha) => new()
    {
        Id = Guid.NewGuid(),
        EntryId = entry.Id,
        MilestoneId = opportunity.Milestones.Single(m => m.Order == number - 1).Id,
        Via = "tag",
        Ref = $"m{number}",
        CommitSha = sha,
        ClaimedAtUtc = at,
    };

    private static User NewUser(string email, string displayName, string role, DateTimeOffset now)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            PasswordHash = "",
            Role = role,
            CreatedAtUtc = now,
            EmailConfirmedAtUtc = now, // demo addresses have no inbox to prove
        };
        user.PasswordHash = Hasher.HashPassword(user, Password);
        return user;
    }
}
