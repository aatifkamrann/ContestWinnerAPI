using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Leaderboard;
using WinnersPortal.Services.Preview;

namespace WinnersPortal.Services.Help;

/// <summary>
/// Every piece of field-level help the portal serves.
///
/// Coverage rule — a field earns a topic when a wrong value <b>costs money,
/// breaks an integration, cannot be undone, or uses domain jargon</b> the
/// reader may not share. Help on every field trains people to ignore help on
/// the fields that matter, so branding and other self-evident inputs
/// deliberately have none.
///
/// Settings marked <c>HelpRequired</c> in SettingsRegistry must have a topic
/// here with the id <c>settings.&lt;key&gt;</c>; HelpCoverageTests fails the
/// build otherwise, which is what keeps this in step as the portal grows.
/// </summary>
public static class HelpRegistry
{
    /// <summary>The connection badge, as each connection's setups topic ends.</summary>
    private const string SetupBadge =
        "The answer stays on the setup's card as its connection badge — Connected or Connection failed, for every "
        + "administrator — until a value of the setup is saved differently, when it reads Changed since test.";

    public static readonly IReadOnlyList<HelpTopic> All =
    [
        // ---------------------------------------------------------------
        // Branding — self-evident except for the one key that breaks links
        // ---------------------------------------------------------------
        new("settings.branding.publicUrl",
            "The address users reach this portal's pages at. Every link in outgoing email is built on it.",
            "Scheme and host, no trailing slash. Email is composed inside your network, where the portal's own "
            + "address is not what your users type — this is the outside address, exactly as a browser shows it. "
            + "When the API has an address of its own (API URL, below), this is also the one origin the API "
            + "accepts a page's calls from, and the session cookie is scoped to the parent the two share.",
            Why: "Wrong here means every emailed button points somewhere broken, and nobody who clicks one tells "
                 + "you — they just stop coming back. The messages themselves still send, so nothing errors.",
            Example: "https://web.crm.com"),
        new("settings.branding.apiUrl",
            "Where the browser reaches the API, when that is a different address from the pages. Blank means the same one.",
            "Scheme and host only, no path. Leave it blank wherever the pages and /api/* share a hostname — behind "
            + "the compose proxy, on a single IIS site. Set it where the API is served from its own hostname: every "
            + "page then sends the browser there for its calls, the API accepts them from the Web URL's origin, "
            + "and the session cookie is scoped to the parent the two hostnames share — so make them one label "
            + "each under one domain, such as web.crm.com and api.crm.com; a save refuses an API URL under any "
            + "other domain, since a browser will not share the cookie across two. If the API has to run on "
            + "another company's server or domain, give it an alias under the pages' domain instead — a DNS "
            + "CNAME such as api.crm.com pointing at wherever it runs, with a certificate for that name on that "
            + "server — and enter the alias here; where the API actually lives never matters, only the name the "
            + "browser uses. The other way out is to leave this blank and have the pages' own site proxy /api/* "
            + "to the API. In force on the next request, no restart; anyone already signed in signs in again "
            + "for the cookie to take its new scope.",
            Why: "Two hostnames are two origins, and a browser lets a page call another origin only when that "
                 + "origin says so and sends the cookie only when asked. Wrong here and every page loads but "
                 + "every call from it fails, with the reason in the browser's console and nowhere on screen.",
            Example: "https://api.crm.com"),

        // ---------------------------------------------------------------
        // AI automation
        // ---------------------------------------------------------------
        new("settings.setups.ai",
            "Several AI providers or keys. One is active at a time, and every AI call goes to it.",
            "Each setup is one provider, API key and model. Only one setup is active at a time — switching one on "
            + "switches the others off — and every AI call goes to it. When the active provider rejects the key, "
            + "lacks the model, is over its quota or is down, the call fails as before; no other provider is asked.\n\n"
            + "The master switch, the daily call limit, the code-excerpt switch and every feature switch apply "
            + "whichever provider is active.\n\n"
            + "Test provider proves one setup at a time, active or not, and counts one call. " + SetupBadge,
            Why: "The active setup is sent opportunity briefs, profile text and, when allowed, code excerpts under its own "
                 + "provider's terms. Only make a provider active whose terms you accept for that data."),

        new("settings.ai.enabled",
            "Master switch. When off, no AI feature runs and no data leaves this server.",
            "This overrides every individual AI feature below. Turning it off stops queued AI jobs from running, "
            + "hides AI-generated panels in the UI, and guarantees nothing is sent to an external model provider.\n\n"
            + "Everything the portal does without AI keeps working exactly as before — AI only ever adds drafts for "
            + "a person to review, so switching it off never blocks an opportunity, an entry, or a payment.",
            Why: "This is the switch to reach for if you are unsure about sending data to a third party, or if a "
                 + "provider outage is generating noise. It is safe to toggle at any time."),

        new("settings.ai.provider",
            "Which model provider handles language tasks: Google Gemini, Anthropic Claude or OpenAI.",
            "Choose the service used for text generation — entry digests, milestone extraction, brief coaching, "
            + "narratives, and SEO drafts. The spam scan never uses a provider; it runs on this server.\n\n"
            + "Gemini has a free tier; Anthropic and OpenAI answer only once the account holds credit. Every AI "
            + "call here is a background job rather than something a visitor waits on, so modest rate limits are "
            + "fine. Verify the provider's current pricing and free-tier limits before you rely on them; they change.",
            Why: "Switching provider changes cost, rate limits, and where your data is processed. Existing generated "
                 + "content is kept — only future jobs use the new provider.",
            Example: "Google Gemini"),

        new("settings.ai.apiKey",
            "API key for the selected provider. Stored encrypted and never shown again.",
            "Create this in your provider's console and paste it here. Once saved it is encrypted with the portal's "
            + "data-protection keys; the settings screen will only ever tell you whether a key is set, never what it is.\n\n"
            + "An API key is billed per call, separately from any chat subscription: a ChatGPT Plus or Pro plan "
            + "gives no API access, and neither does a Claude or Gemini app subscription. Create the key at the "
            + "provider's developer console (platform.openai.com, console.anthropic.com, Google AI Studio) and, "
            + "for OpenAI and Anthropic, add credit there before the first test.\n\n"
            + "To replace a key, type the new one over the field. To remove it, use the clear button.",
            Why: "Without a valid key every language-based AI feature fails silently into its non-AI fallback. Treat "
                 + "the key as a secret — anyone holding it can spend your provider credit."),

        new("settings.ai.model",
            "Model identifier passed to the provider. Leave blank to use the recommended default.",
            "Set this only if you have a reason to pin a specific model — a newer release, or one with a larger "
            + "context window for long briefs. Blank resolves to gemini-3.6-flash, claude-sonnet-5 or gpt-5.5, "
            + "by provider.",
            Why: "An identifier the provider does not recognise makes every AI job fail. Blank is the safe choice.",
            Example: "gpt-5.5"),

        new("settings.ai.dailyCallLimit",
            "Hard ceiling on provider calls per day. The portal stops before your free tier does.",
            "Counted per calendar day in UTC and reset at midnight. When the ceiling is reached, remaining AI jobs are "
            + "skipped rather than queued, and the features they feed simply do not appear until the next day.\n\n"
            + "An opportunity with thirty entrants consumes well under a hundred calls across its whole lifecycle, so the "
            + "default leaves considerable headroom.",
            Why: "This is what keeps 'free' honest. Without a ceiling, a burst of entries could exhaust a free tier "
                 + "or run up a bill on a paid one.",
            Example: "200"),

        new("settings.ai.sendCodeToProvider",
            "Allows entrant code excerpts to be sent to the external model provider.",
            "Entry review digests are far more useful when the model can see diffs, file trees, and README content. "
            + "That means excerpts of entrants' code leave this server and are processed by a third party.\n\n"
            + "The portal never sends whole repositories — only diffs, structure, and summaries — which is both "
            + "cheaper and less exposure. When this is off, digests fall back to metadata the portal already holds: "
            + "commit counts, languages detected, milestone coverage, and whether tests exist.",
            Why: "Entrants' repositories are private and their work is unpaid until an award is made. Enabling this "
                 + "is a disclosure obligation — say so in your terms of service, and consider making it a per-opportunity "
                 + "opt-in rather than a portal-wide default."),

        new("settings.ai.features.entryDigest",
            "Drafts a per-entry summary to speed up the client's code review.",
            "For each entry, drafts a short brief covering what was built, the stack detected, milestone coverage, "
            + "whether tests and documentation are present, and anything a reviewer should look at closely.\n\n"
            + "The digest is a reading aid, never a score. It is shown to the client alongside the code, not instead "
            + "of it.",
            Why: "Reviewing every entrant's repository by hand is the slowest step in the whole opportunity, so this is "
                 + "the highest-value AI feature in the portal. It never influences who wins — the client decides."),

        new("settings.ai.features.milestoneExtraction",
            "Turns a prose opportunity brief into a draft milestone checklist.",
            "Reads the brief on the opportunity form and proposes the milestones that become the entrants' board. "
            + "The client edits and approves the list before the opportunity is published — nothing is created "
            + "automatically. One call per press, answered inside the request, on create and on edit alike.",
            Why: "The milestone board is what the webhook system tracks against, so a good starting list makes every "
                 + "downstream progress signal more accurate."),

        new("settings.ai.features.briefCoach",
            "Flags vague or incomplete opportunity briefs before they go live.",
            "Checks a draft brief for the things that cause arguments later: unstated technology constraints, "
            + "ambiguous deliverables, missing acceptance criteria, no clear definition of done.\n\n"
            + "Suggestions appear beside the brief while the client writes — it reads the form, so it works "
            + "before a draft has ever been saved. Advisory, dismissable, one call per press.",
            Why: "Disputed outcomes usually trace back to a brief that meant different things to the client and the "
                 + "entrant. Catching that before publication is much cheaper than arbitrating it afterwards."),

        new("settings.ai.features.spamFilter",
            "Flags low-effort and duplicate entries for review. Runs entirely on this server.",
            "Combines local file-tree similarity with simple heuristics — empty repositories, README-only "
            + "submissions, entries whose files are near-identical to another entrant's — and flags them for a "
            + "human to look at.\n\n"
            + "No model provider is involved: nothing leaves this server and no call quota is consumed.",
            Why: "Entry is open with no cap on entrants, so some filtering is necessary at scale. Flagged entries are "
                 + "never rejected automatically — the client decides what a flag means."),

        new("settings.ai.features.progressNarrative",
            "Turns the commit stream into readable progress notes on the milestone board.",
            "Once a day, summarises each entrant's commits into a plain-language note so the client can follow "
            + "progress without reading raw commit logs. Batched deliberately to keep provider usage low.",
            Why: "Purely additive. If it is off, the board still shows commits, milestones, and timestamps as usual."),

        new("settings.ai.features.seoMetadata",
            "Drafts page titles and meta descriptions for public opportunity pages.",
            "Generates the metadata search engines read for each published opportunity, then leaves it editable. "
            + "Read from the form on request, one call per press.",
            Why: "Freelancers find opportunities by searching, so this feeds the same discoverability the public pages "
                 + "exist to serve. Drafts are editable — a human keeps the final say on public wording."),

        new("settings.ai.features.standingNotes",
            "Drafts reading notes for an opportunity's progress board — what each entrant's standing shows.",
            "On request from the opportunity's client (or an administrator), the model is given every entrant's "
            + "standing score, rank, band and the explained breakdown, and drafts a short note per row plus a "
            + "summary of the board. The score and the order are computed by the portal; the model only puts "
            + "them into words.\n\n"
            + "Runs on request, one call per opportunity per draft, cached until the board changes. Nothing from "
            + "inside a repository is sent.",
            Why: "This is the feature that reads beside the remove button, so its wording matters: it is told "
                 + "never to recommend keeping or removing anyone, and the client decides. Switching it off "
                 + "leaves the standing score and breakdown on the board exactly as they are."),

        new("settings.ai.features.categorySuggestion",
            "Reads a draft brief and suggests which kind of work the opportunity is.",
            "On the opportunity form, beside the category picker, on create and on edit alike: the client presses once, "
            + "the model reads the title and brief as typed, and a suggestion appears with the phrase that decided "
            + "it. The client confirms it, changes it, or ignores it — nothing is set for them.\n\n"
            + "One provider call per press, and the button will not spend a second one on an unedited brief. It is "
            + "the one AI feature answered inside the request rather than queued, because somebody is waiting for "
            + "it. Switched off, the picker is simply a picker.",
            Why: "An opportunity filed under the wrong kind is browsed by the wrong people, and the client is the one "
                 + "who pays for that in weak entries. This is the cheapest place to catch it."),

        new("settings.ai.features.requirementsSuggestion",
            "Drafts the technical requirements table on the opportunity form from the brief above it.",
            "On the opportunity form, under the requirements table, on create and on edit alike: the client presses "
            + "once, the model reads the title, brief, kind of work and required skills as they stand on the "
            + "form — saved or not — and offers the constraints the brief states or clearly implies, one row "
            + "each. The client adds the rows they want, edits them, or ignores them; nothing is written into "
            + "the table for them, and a constraint the brief does not support is not invented.\n\n"
            + "One provider call per press, answered inside the request, and the button will not spend a second "
            + "call until something it read has changed. Switched off, the table offers its usual row names as "
            + "chips and nothing more.",
            Why: "The constraints a brief buries in prose are the ones entrants miss. Pulling them into rows "
                 + "the client reads before publishing is cheap; a dispute over an unstated one is not."),

        new("settings.ai.features.criteriaSuggestion",
            "Drafts the scoring rubric on the opportunity form from the brief, requirements and milestones above it.",
            "On the opportunity form, under the evaluation table, on create and on edit alike: the client presses "
            + "once, the model reads the brief, the requirements and the milestone checklist as they stand on "
            + "the form — saved or not — and offers three to six criteria with the points each carries, adding "
            + "up to a hundred. The client takes the rubric, edits every line, or keeps their own; nothing is "
            + "written for them, and the model judges nothing — it drafts the rubric the client will judge "
            + "by.\n\n"
            + "One provider call per press, answered inside the request, and the button will not spend a "
            + "second call until something it read has changed. Switched off, the standard five-line rubric "
            + "is still one press away.",
            Why: "A rubric written after the entries arrive is a rubric written around them. Drafting it from "
                 + "the brief, before publishing, is what makes the promise to entrants a real one."),

        new("settings.ai.features.profileSummary",
            "Drafts a member’s About from the rest of their profile form, on request.",
            "On the profile form, under About You: the member presses once, the model reads the title, skills, "
            + "languages and past work as they stand on the form — saved or not — and offers a first-person "
            + "summary. They use it, edit it, or keep their own; nothing is written for them.\n\n"
            + "One provider call per press, answered inside the request, and the button will not spend a second "
            + "call on an unedited form. The request carries the profile fields and nothing else — never an "
            + "email, never payment details. Switched off, the box is not shown.",
            Why: "A blank About is the commonest reason a strong profile reads as an empty one. A draft to "
                 + "react to is easier to write against than a blank box, and it stays theirs."),

        new("settings.ai.features.projectApproach",
            "Drafts how a freelancer would approach an opportunity, on the application’s approach step.",
            "The “Generate approach” button on step 5 of applying to compete. The model reads the brief — its "
            + "required skills, milestones and requirements — and the applicant’s own profile, and drafts the "
            + "phases, the architecture decisions, the risks and the delivery strategy in the first person. "
            + "Only the past work that fits the brief travels with the request; nothing else on the profile does. "
            + "The applicant takes the draft, edits it or keeps their own — nothing is submitted for them.\n\n"
            + "One provider call per press, counted against the daily ceiling.",
            Why: "An application is judged on its plan, and a blank box is where most applicants stall. A draft "
                 + "they must read and edit is a start, not a proposal written for them."),

        new("settings.ai.features.applicationEvaluation",
            "Words the evaluation box on an application to compete, for the client and the applicant.",
            "When an application is filed, the portal reads its own assessment — the match figure, its word, "
            + "and a strength or a risk for each line that reads strong or weak — and queues one call for the "
            + "words. The model is given those lines by id and asked for a short phrase about this application "
            + "for each, and one note saying what the client should open first. It cannot add a line, drop one, "
            + "write a figure or say whether to select: the lines and the figure are on the page whether or not "
            + "it has answered, and the client decides.\n\n"
            + "One provider call per application, and none a second time for the same one.",
            Why: "The client reads a row per applicant. A phrase that points at the approach or the attached "
                 + "past work saves them opening everything; a verdict would be the portal choosing for them."),

        new("settings.ai.features.profileReview",
            "Reads a freelancer’s saved profile against the open opportunities and suggests what to add.",
            "The box under Profile Strength on a freelancer’s own Preview tab: what their profile is strongest "
            + "for, and up to three improvements, each with a figure for how many more of the open opportunities it "
            + "would open to them. The lines and the figures are the portal’s arithmetic — every open opportunity’s "
            + "merit floor and required skills, read against the profile — and are on the page whether or not "
            + "the model has answered; the model is given them and asked only to put each into a sentence, "
            + "and to say what the profile is strongest for.\n\n"
            + "One provider call per change: the words are re-made when the profile or the open opportunities "
            + "change and served from the last answer otherwise. Nobody presses anything for it; opening the "
            + "Preview tab is what asks. The email and the payment details never travel — only that a way to "
            + "be paid exists. Switched off, the box is not shown at all, arithmetic included.",
            Why: "The member cannot see what the open opportunities are asking for from their own page, and the "
                 + "commonest gap between a profile and an opportunity is one skill nobody listed. This is the "
                 + "cheapest place to say so."),

        new("settings.ai.features.recommendedMatching",
            "Reads each freelancer’s own skills to decide which opportunities are their usual kind of work.",
            "The Recommended colouring on the browse page. Once per freelancer — not once per opportunity — the model "
            + "reads the skills and headline on their profile and says which categories those cover; the portal "
            + "then matches that against every opportunity without calling anything. A reading is re-read only when "
            + "they change their profile.\n\n"
            + "Switched off, nothing is coloured as outside anybody’s usual work: the entry rules (merit score "
            + "and required skills) are arithmetic and go on working exactly as before.",
            Why: "It is the one AI feature that reads a member’s own profile, so it is worth knowing what it "
                 + "does and does not do: it never blocks an entry, never scores anybody, and colours a card at "
                 + "most. Cost scales with how often members edit their profiles, not with how often they browse."),

        // ---------------------------------------------------------------
        // GitHub
        // ---------------------------------------------------------------
        new("settings.setups.github",
            "Several GitHub Apps, each on its own organization. One is active: new repositories are made through it.",
            "Each setup is a whole GitHub App — its ID, key, organization, webhook secret and client pair — and the "
            + "transfer token that hands a winner's repository over. Only one "
            + "setup is active at a time — switching one on switches the others off. Entrant repositories are created "
            + "through the active setup, and Connect GitHub signs people in with its client ID and secret. If the "
            + "active setup is incomplete or GitHub refuses it, nothing falls back to another setup.\n\n"
            + "A repository that already exists is always reached through the setup for its organization, even an "
            + "inactive one: making another App active stops new repositories going to the old organization, it does "
            + "not cut entrants off from the ones they have. Webhook deliveries are checked against every setup's "
            + "secret for the same reason.\n\n"
            + "Test GitHub proves one setup at a time, active or not. " + SetupBadge,
            Why: "A setup whose organization still holds entrant repositories cannot be removed — their boards, freezes "
                 + "and the winner's transfer all go through it. Leave it inactive instead."),

        new("settings.github.appId",
            "Numeric ID of your GitHub App, shown on its settings page.",
            "Not the same as the client ID. GitHub shows it as 'App ID' at the top of the app's General page.",
            Why: "A wrong ID means every GitHub call is rejected and no entrant repository can be created.",
            Example: "1234567",
            LearnMoreUrl: "https://docs.github.com/en/apps/creating-github-apps"),

        new("settings.github.appSlug",
            "URL name of your GitHub App, taken from its public page address.",
            "The last path segment of github.com/apps/<slug>. Used to build install and authorisation links.",
            Why: "A wrong slug sends clients and entrants to a broken installation link, even though the rest of the "
                 + "integration works.",
            Example: "astrik-winners-portal"),

        new("settings.github.organization",
            "The GitHub organisation that owns every entrant repository.",
            "Each entrant gets a private repository created inside this organisation. The portal's app must be "
            + "installed on it with permission to create repositories.\n\n"
            + "Use an organisation dedicated to opportunities rather than your main company org — entrants are granted "
            + "access to their own repositories inside it.",
            Why: "Repository ownership determines who controls the code before an award is paid. Changing this later "
                 + "does not move repositories that already exist.",
            Example: "astrik-opportunities"),

        new("settings.github.privateKey",
            "The PEM private key generated for your GitHub App. Stored encrypted.",
            "Generate it on your GitHub App's settings page and download the .pem file. Paste the entire contents "
            + "including the BEGIN and END lines.\n\n"
            + "GitHub will not show it to you again. If it is lost, generate a replacement — the old key stops "
            + "working the moment you do.",
            Why: "This key signs every request the portal makes as the app. Without it, repository provisioning, "
                 + "entrant invitations, and webhook verification all fail.",
            Example: "-----BEGIN RSA PRIVATE KEY-----"),

        new("settings.github.webhookSecret",
            "Shared secret GitHub signs webhook deliveries with. Stored encrypted.",
            "Must match the secret configured on the GitHub App's webhook settings exactly. The portal rejects any "
            + "delivery whose signature does not verify.",
            Why: "Without a matching secret the portal drops every webhook, so milestone boards stop updating and "
                 + "commits stop appearing — with no obvious error on the GitHub side."),

        new("settings.github.clientId",
            "The App's OAuth client ID, used by the Connect GitHub button.",
            "Shown as 'Client ID' on the app's General page — a string starting with Iv, not the numeric App ID "
            + "above. It powers the user-authorisation flow clients use to connect their GitHub account.\n\n"
            + "The app's callback URL must be set to https://<your host>/api/github/callback.",
            Why: "Without it clients cannot connect a GitHub account, and without a connected account the portal "
                 + "refuses to announce a winner — the winning repository would have nowhere to transfer.",
            Example: "Iv1.a1b2c3d4e5f6a7b8"),

        new("settings.github.clientSecret",
            "The App's OAuth client secret. Stored encrypted.",
            "Generate it on the app's General page under 'Client secrets'. It is paired with the client ID to "
            + "complete the Connect GitHub flow; GitHub shows it only once.",
            Why: "Anyone holding this can complete authorisations as your app. If it leaks, revoke it on GitHub "
                 + "and paste the replacement here — the old one stops working immediately."),

        new("settings.github.transferToken",
            "An organization owner's classic token, used only to hand a paid winner's repository to the client. Stored encrypted.",
            "GitHub does not let an App transfer a repository, whatever permissions it has — the call is answered "
            + "\"Resource not accessible by integration\". Only a person can, so the handover goes out on this token "
            + "and everything else still goes through the App.\n\n"
            + "Sign in as an owner of the organization — best an account kept for the portal alone — and create it "
            + "under Settings, Developer settings, Personal access tokens, Tokens (classic), with the repo scope ticked. "
            + "A fine-grained token will not do: GitHub refuses transfers from those too.\n\n"
            + "Test GitHub checks it against its scratch repository: GitHub accepts it, it has the repo scope, and its "
            + "account has admin rights in the organization.",
            Why: "Without a working token a client can pay and never receive the code: the transfer waits and the award "
                 + "shows why. A classic token reaches every repository its account can, which is why that account "
                 + "should be one kept for this. Give the token an expiry and note the renewal — an expired token "
                 + "stops handovers the same way a missing one does.",
            Example: "ghp_…",
            LearnMoreUrl: "https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-personal-access-token-classic"),

        // ---------------------------------------------------------------
        // Storage
        // ---------------------------------------------------------------
        new("settings.setups.storage",
            "Several object stores. One is active: new files go to it; each file is read from its own store.",
            "Each setup is a whole S3-compatible store: endpoint, public URL, bucket, region and keys. Only one setup "
            + "is active at a time — switching one on switches the others off. Brief attachments, entrant uploads "
            + "and packaged ZIPs go to the active store, and every file remembers the store it went to, so its "
            + "downloads, previews and deletes go back there whichever store is active later.\n\n"
            + "Files stored before there was a choice belong to Main.\n\n"
            + "Test storage proves one setup at a time, active or not: a file written, read back and deleted from the "
            + "server — the path every upload takes, since files reach the store through the portal and the bucket "
            + "needs no CORS rule. What it cannot prove is that a browser reaches the Public base URL, which is "
            + "where downloads and previews open from. " + SetupBadge,
            Why: "A store that still holds files cannot be removed — those files would stop downloading. Leave it "
                 + "inactive instead: nothing new goes there, and everything already there stays reachable. The portal "
                 + "never moves files between stores."),

        new("settings.storage.endpoint",
            "Base URL of the S3-compatible storage service.",
            "Points at MinIO in local development and at your object store in production. Include the scheme; omit "
            + "the bucket name, which is configured separately.",
            Why: "A wrong endpoint breaks uploads, thumbnails, and entry ZIP packaging — usually only noticed when "
                 + "someone tries to download a submission.",
            Example: "http://storage:9000"),

        new("settings.storage.bucket",
            "Bucket that holds uploads, thumbnails, and packaged entries.",
            "Must be writable by the credentials below. The storage test creates it when it is missing and the "
            + "credentials are allowed to; on locked-down keys, create it in the store first.",
            Why: "Renaming this does not move existing objects; previously stored files become unreachable.",
            Example: "winnersportal"),

        new("settings.storage.accessKey",
            "Access key ID of a key made on the store itself (on MinIO, a user). Entering it here does not create it.",
            Why: "Wrong credentials fail at upload time rather than at save time, so test an upload after changing them."),

        new("settings.storage.secretKey",
            "Secret access key for the storage service. Stored encrypted.",
            Why: "Anyone holding this can read and delete every uploaded file, including entrants' packaged work."),

        new("settings.storage.publicUrl",
            "The storage address browsers open files from. Every signed download and preview link is built on it.",
            "A download or a preview comes to the browser straight from the object store — the API only signs a "
            + "short-lived link, and the address is part of each signature. Inside the compose network the API "
            + "reaches storage at an internal hostname no browser can resolve; this is the outside address. In "
            + "production it is your S3, R2 or CDN domain. Under a portal served over https it must be https too: "
            + "a browser loads nothing from an http address into an https page, and the storage test refuses one. "
            + "Uploads do not use it: they reach the store through the portal, so the bucket needs no CORS rule."
            + "\n\nLeave it blank only when the endpoint above is itself browser-reachable.",
            Why: "Wrong here means every download and preview fails in the browser while uploads and the storage "
                 + "test — which both reach the store from the server — still work. That mismatch is this field.",
            Example: "http://localhost:9000"),

        new("settings.storage.region",
            "The region name used when signing requests — a required ingredient of every signature, not a location.",
            "MinIO accepts the default. Cloudflare R2 expects \"auto\". AWS S3 wants the bucket's real region.",
            Why: "A region the store does not expect makes every signature invalid, so every upload and download "
                 + "fails with an access error that looks like wrong credentials.",
            Example: "us-east-1"),

        // ---------------------------------------------------------------
        // Email
        // ---------------------------------------------------------------
        new("settings.setups.email",
            "Several ways to send email. One is active at a time, and every email goes through it.",
            "Each setup is one way out — an SMTP server, or Mailgun or Brevo over their HTTPS APIs — with its own "
            + "credentials and From address. Only one setup is active at a time — switching one on switches the "
            + "others off — and the outbox sends everything through it, from its From address. When the active "
            + "setup cannot take mail (the server refuses the connection, the service refuses the key, or it is "
            + "down), mail waits in the outbox and is tried again after a minute, then less often, up to every "
            + "fifteen minutes; it does not move to another setup, and saving the setup differently tries again at "
            + "once.\n\n"
            + "Send test email proves one setup at a time, active or not, so a replacement can be checked before it "
            + "is made active. " + SetupBadge,
            Why: "With no setup active, or an active one missing its host or key, no email leaves: invitations and "
                 + "reset links are refused up front, and notifications wait in the outbox until they expire after "
                 + "three days."),

        new("settings.email.provider",
            "How this setup sends: through an SMTP server, or through Mailgun's or Brevo's HTTPS API.",
            "SMTP works with any mail server or relay, and is what the compose stack's Mailpit speaks. Mailgun and "
            + "Brevo are sent to over HTTPS instead, which needs no SMTP port: many hosts block or throttle 25, 465 "
            + "and 587 outbound (EC2 among them), while 443 is always open. Each API answers every send with the "
            + "service's own words, so a refusal reaches the outbox and the test as a reason rather than a silence.\n\n"
            + "The screen shows only the fields the chosen way reads: SMTP host, port, user and password for SMTP; "
            + "the API key, sending domain and region for Mailgun; the API key for Brevo. The From address belongs "
            + "to all three. Values of the other ways are kept, so switching back needs nothing retyped.\n\n"
            + "Both services also run an SMTP relay. Using it instead means choosing SMTP here and typing the "
            + "relay's host and login.",
            Why: "Every email goes through the choice the active setup makes. Changing it changes where every "
                 + "invitation, reset link and notification is sent from — use Send test email before saving it on "
                 + "the active setup.",
            Example: "mailgun"),

        new("settings.email.apiKey",
            "Mailgun or Brevo only: the key the service issued this account. Stored encrypted and never shown again.",
            "Mailgun: a private API key, or a domain sending key limited to the sending domain below — the "
            + "narrower one is the better choice. It is sent as a login with the user \"api\".\n\n"
            + "Brevo: an API key from SMTP & API → API keys — it starts xkeysib-. The SMTP key on the same page "
            + "(xsmtpsib-) is a relay password, not an API key, and Brevo refuses it here. If Brevo's Authorised "
            + "IPs setting is on, add this server's outbound address there too, or every send is refused as "
            + "unauthorised.",
            Why: "Anyone holding it can send mail as your domain at your expense, and read what the account has sent "
                 + "— treat it like the SMTP password."),

        new("settings.email.mailgunDomain",
            "Mailgun only: the sending domain, exactly as it is listed in Mailgun.",
            "The domain you added and verified in Mailgun — usually a subdomain such as mg.example.com. The From "
            + "address should be on it, or on the domain it belongs to, so SPF and DKIM line up. A sandbox domain "
            + "(sandbox….mailgun.org) delivers only to the recipients you have authorised on it, so members would "
            + "not hear from the portal.\n\n"
            + "Turn click tracking off for this domain in Mailgun: it rewrites every link in an email, the "
            + "password-reset links included.",
            Why: "A domain the account does not hold, or one in the other region, is refused with 404 — no email "
                 + "leaves until it matches.",
            Example: "mg.example.com"),

        new("settings.email.mailgunRegion",
            "Mailgun only: the region the sending domain was added in — US or EU.",
            "Mailgun keeps each domain in one region, with its own API address. It is shown against the domain in "
            + "Mailgun's dashboard; the EU region is api.eu.mailgun.net.",
            Why: "The wrong region cannot see the domain: every send is refused, with 401 or 404, until it matches."),

        new("settings.email.smtpHost",
            "SMTP only: the server used for all outbound email.",
            "The compose stack points this at Mailpit, which catches every message so nothing reaches real inboxes "
            + "during development.",
            Why: "Every invitation, deadline warning, and award notification goes through here. If it is wrong, "
                 + "entrants silently stop hearing from the portal.",
            Example: "mail"),

        new("settings.email.smtpPort",
            "SMTP only: the server's port. Commonly 25, 465, 587, or 1025 in development.",
            Why: "A reachable host on the wrong port produces connection timeouts on every send.",
            Example: "1025"),

        new("settings.email.fromAddress",
            "Address that portal email is sent from.",
            "Use a domain you control and that is authorised to send — SPF and DKIM records matter here. With "
            + "Mailgun, an address on the sending domain; with Brevo, a sender or domain verified under Senders, "
            + "Domains & Dedicated IPs — Brevo refuses any other.",
            Why: "An unauthenticated sending domain sends opportunity email straight to spam, which reads to entrants "
                 + "as the portal being broken.",
            Example: "no-reply@winnersportal.example"),

        new("settings.email.smtpUser",
            "SMTP only: the username to sign in with. Leave blank if the server does not require it.",
            "On Brevo's relay (smtp-relay.brevo.com, port 587) it is the Login shown under SMTP & API → SMTP — "
            + "often an address ending @smtp-brevo.com rather than the account's own email. Spaces around it are "
            + "dropped when it is saved.",
            Why: "Development mail catchers accept anonymous sending; nearly every production provider does not."),

        new("settings.email.smtpPassword",
            "SMTP only: the password to sign in with. Stored encrypted.",
            "Kept exactly as typed, spaces included. On Brevo's relay it is an SMTP key from SMTP & API → SMTP "
            + "(xsmtpsib-…) — not the API key (xkeysib-…), which the Brevo HTTPS API choice takes, and not the "
            + "account password.",
            Why: "Often an API key rather than an account password — check your provider's documentation before "
                 + "pasting a login password here."),

        // ---------------------------------------------------------------
        // Phone messages — the SMS gateway
        // ---------------------------------------------------------------
        new("settings.setups.phone",
            "Several SMS gateways. One is active at a time, and every confirmation code is texted through it.",
            "Each setup is one gateway: a provider the portal knows, or a request you write yourself. Only one setup "
            + "is active at a time — switching one on switches the others off — and every confirmation code goes "
            + "through it. When the active gateway refuses a text, the member is told the code went by email instead; "
            + "no other gateway is tried.\n\n"
            + "Send test text proves one setup at a time, active or not, so a gateway can be checked before it is "
            + "made active. " + SetupBadge,
            Why: "With no setup active, or an active one set to None, codes go by email only and the join form asks "
                 + "for no phone number."),

        new("settings.phone.provider",
            "Which SMS service texts the confirmation codes. Off means codes go by email only.",
            "New accounts confirm with a code emailed to them and, when this is set, a second one texted to "
            + "their phone; either code confirms. Pick the service you have an account with — the portal "
            + "already knows its address and the shape of its request, so all you supply is the key below.\n\n"
            + "What the choices are for:\n"
            + "• TextBee and httpSMS turn an ordinary Android phone with a SIM into the sender. Both are open "
            + "source with a free tier, and for a portal sending a handful of codes a day they cost the price "
            + "of an SMS bundle. Install their app on a spare handset, register it, and paste the key.\n"
            + "• SendPK is a Pakistani aggregator on a shared short code — the cheap route if your members "
            + "are in Pakistan.\n"
            + "• Telnyx is a global carrier API, worth it when members are spread across many countries.\n"
            + "• Something else leaves the three Custom fields below to you, which reaches any gateway that "
            + "takes one HTTP request.",
            Why: "Cost differs by more than an order of magnitude by route, not by quality: texting a "
                 + "Pakistani number through a global carrier API runs around US$0.47 a message, a Pakistani "
                 + "aggregator around Rs 4.75, and a SIM in a phone the price of a bundle. Pick for where "
                 + "your members are. Whatever you choose, use Send test text afterwards — a wrong key is "
                 + "otherwise discovered by somebody who cannot finish signing up.",
            Example: "textbee"),

        new("settings.phone.gatewayApiKey",
            "The key or token for the service chosen above. Stored encrypted and never shown again.",
            "Every provider issues one from its own dashboard. It is sent in whichever way that provider "
            + "expects — a header, a bearer token, or a field inside the request — which the portal already "
            + "knows.\n\n"
            + "For a provider that authenticates with two values rather than one (an account SID and a "
            + "token, say), join them with a colon: sid:token. They are then sent as an HTTP basic login, "
            + "which is what those providers expect.",
            Why: "Anyone holding it can send messages at your expense, or from your phone — treat it like "
                 + "the SMTP password."),

        new("settings.phone.senderId",
            "Who the text appears to come from. What goes here depends on the provider; some need nothing.",
            "httpSMS wants the sending phone's own number in international form. SendPK wants a sender ID "
            + "or short code approved on your account. Telnyx wants one of your Telnyx numbers or a "
            + "messaging profile ID. TextBee needs nothing here — it sends from the SIM in the phone.\n\n"
            + "Leave it empty when the provider has no such notion.",
            Why: "A sender ID that is not registered with the provider is refused by them, or silently "
                 + "dropped by the operator's network — the message simply never arrives. In Pakistan an "
                 + "alphabetic sender mask has to be registered before it will deliver at all.",
            Example: "+923001234567"),

        new("settings.phone.gatewayUrl",
            "Only for \"Something else\": the address the portal POSTs each text to.",
            "Ignored unless the provider above is \"Something else\". Give the full https address from your "
            + "gateway's documentation.",
            Why: "The three Custom fields are how a provider the portal has never heard of still works — "
                 + "including one you run yourself. They are inert while a named provider is chosen.",
            Example: "https://sms.example.com/api/send"),

        new("settings.phone.gatewayAuthHeader",
            "Only for \"Something else\": which request header carries the key.",
            "Set it to Authorization for a gateway wanting a bearer token or a basic login; any other name "
            + "is sent as given, with the key as its value. Leave it as it is when the key belongs inside "
            + "the request body instead — put {key} in the body below and it goes there rather than here.",
            Example: "x-api-key"),

        new("settings.phone.gatewayBody",
            "Only for \"Something else\": the request itself. {to}, {text}, {from} and {key} are filled in.",
            "Write it the way your gateway's documentation shows. A body starting with { is sent as JSON; "
            + "anything else is sent as form fields, which is what most of the older gateways want.\n\n"
            + "Each placeholder is escaped for whichever of the two it is, so a message containing a quote "
            + "or an ampersand cannot break the request apart.",
            Why: "A body the gateway does not recognise is refused in its own words, which Send test text "
                 + "shows you verbatim.",
            Example: "api_key={key}&sender={from}&mobile={to}&message={text}"),

        // ---------------------------------------------------------------
        // Opportunity policy
        // ---------------------------------------------------------------
        // ---------------------------------------------------------------
        // Identity verification
        // ---------------------------------------------------------------
        new("settings.setups.identity",
            "Several verification providers or keys. One is active at a time, and every verification runs through it.",
            "Each setup is one provider account: its API key, the workflow it runs and the secret its webhooks are "
            + "signed with. Only one setup is active at a time — switching one on switches the others off — and "
            + "every member who verifies goes through it. Verdicts already recorded stay recorded whichever "
            + "setup is active.\n\n"
            + "Test Didit proves one setup at a time, active or not: the key is accepted, the workflow id is "
            + "well-formed, and the secret is set. " + SetupBadge,
            Why: "The active setup is sent a member's name and email so the provider can address them, and the "
                 + "member then hands the provider their identity document directly. Only make a setup active "
                 + "whose provider's terms you accept for that."),

        new("settings." + Identity.IdentityKeys.Enabled,
            "Master switch. When off, nobody is asked to verify and every door below stands open as before.",
            "Identity verification is a trust control, not a payment one: the portal holds no money, so nothing "
            + "here is required by law. When on, the three switches below say which doors ask for it — a client "
            + "publishing, a freelancer applying, a freelancer's payment details reaching a client. A member who "
            + "never reaches one of those doors is never asked.\n\n"
            + "Turning it off later keeps every verdict already recorded; the badges stay and the doors simply "
            + "stop asking. Turning it back on resumes where it left off.",
            Why: "A door that asks for verification stops a member cold until the provider answers, which is "
                 + "minutes for most and a day for a manual review. Switch it on with a working setup and a "
                 + "sentence in the terms, not before."),

        new("settings." + Identity.IdentityKeys.Provider,
            "Which verification service runs the check. Didit is the one the portal knows today.",
            "Didit runs a hosted flow — the member is sent to the provider's page, photographs an identity "
            + "document and their face, and is sent back — and tells the portal the verdict by a signed webhook. "
            + "The portal keeps the verdict and, as proof, the provider's whole decision and copies of the "
            + "document and selfie images in its own file storage, for administrators to read on the member's "
            + "Users page.",
            Why: "The provider is who a member hands their passport to, and the portal now keeps a copy too. "
                 + "Read the provider's terms and retention, configure file storage, and say in your privacy "
                 + "policy that identity documents are kept, before you point members at it."),

        new("settings." + Identity.IdentityKeys.ApiKey,
            "The provider's API key. Stored encrypted and never shown again.",
            "In the Didit console: your application's API key under its settings. Once saved it is encrypted "
            + "with the portal's data-protection keys; the settings screen will only ever tell you whether a key "
            + "is set, never what it is. Didit's first 500 checks in a month are free and there is no minimum, "
            + "but confirm the current terms before you rely on that.\n\n"
            + "To replace a key, type the new one over the field. To remove it, use the clear button.",
            Why: "Without a valid key no verification can start, and every door that requires one stays shut — "
                 + "the member is told the portal cannot verify anyone right now. Treat the key as a secret; "
                 + "anyone holding it can open sessions billed to your account."),

        new("settings." + Identity.IdentityKeys.WorkflowId,
            "The Didit workflow every member runs: which checks, in which order. A UUID from the console.",
            "In the Didit console, Workflows: make one with ID verification, liveness and face match — the free "
            + "trio — publish it, and copy its ID here. Adding AML screening or proof of address to the "
            + "workflow adds a per-check charge; the portal does not need either.\n\n"
            + "Changing the workflow affects members who start after the change; sessions already open finish "
            + "on the workflow they started with.",
            Why: "An unpublished or mistyped workflow makes every start fail with the provider's own error. The "
                 + "test checks the shape of the id; only a real start proves the workflow exists."),

        new("settings." + Identity.IdentityKeys.WebhookSecret,
            "The shared secret Didit signs its webhooks with. Stored encrypted.",
            "In the Didit console, Webhooks: add a destination whose URL is this portal's public address plus "
            + "/api/webhooks/identity, and paste the destination's secret key here. The portal rejects any "
            + "delivery whose signature or timestamp does not verify.\n\n"
            + "A missed delivery is not fatal: the page a member returns to asks the provider for the verdict "
            + "directly, and an administrator can do the same from the member's row.",
            Why: "Without a matching secret every webhook is dropped and verdicts arrive only when a member "
                 + "returns to the portal — a member who closes the provider's tab is never marked verified."),

        new("settings." + Identity.IdentityKeys.RequireForPublish,
            "A client verifies their identity before their first opportunity goes live.",
            "Publish refuses until the client has passed, with a link to verify; drafts are unaffected, and a "
            + "client already verified never sees the question again. The check runs at the moment of "
            + "publishing, so a client can write the whole brief first.",
            Why: "A published opportunity is a promise of money to strangers. Knowing who made the promise is what "
                 + "lets an entrant stake a fortnight on it."),

        new("settings." + Identity.IdentityKeys.RequireForApply,
            "A freelancer verifies their identity before their first application.",
            "The apply form's eligibility step shows the verification as done or owed, with a link, and the "
            + "submit refuses until it is done. Browsing, the profile and the merit score are unaffected. A "
            + "freelancer already verified never sees the question again.",
            Why: "A client picks entrants on a profile; verification is what says the profile is a person. It "
                 + "also keeps one person from entering an opportunity twice under two accounts."),

        new("settings." + Identity.IdentityKeys.RequireForPayments,
            "A freelancer's payment details reach the client who made them a winner only once the freelancer has verified.",
            "Until then that client sees, where the details would be, a line saying they will "
            + "appear once the member has verified — never the details, and never whether any were saved. The "
            + "member and administrators always see them. With this off, the payment section follows the award "
            + "rule alone: the member, administrators and a client who has announced the member as a winner.",
            Why: "Account numbers are what a fraudulent profile is for. Holding them back until the person "
                 + "behind the profile is known keeps a client from paying an award to nobody."),

        // ---------------------------------------------------------------
        // Build host
        // ---------------------------------------------------------------
        new("settings.setups.preview",
            "Several build hosts. One is active at a time, and every milestone build runs on it.",
            "Each setup is one Linux server running the portal's build agent (deploy/preview-host in the source, "
            + "with the launch checklist): its address and the token the agent was installed with. Only one setup "
            + "is active at a time — switching one on switches the others off — and every claimed milestone of a "
            + "opportunity that requires Docker Compose is built on it. A build already handed to a host finishes there.\n\n"
            + "Test build host asks the agent for its health — Docker, Compose, free disk, whether a build is "
            + "running, how many previews are up and which domain it serves them under — and builds nothing. " + SetupBadge,
            Why: "The active host is handed each entrant's repository with a short-lived token to clone it, and runs "
                 + "the entrant's build there. Give it a server of its own, never the portal's: a build runs a "
                 + "stranger's code."),

        new("settings." + PreviewHostKeys.HostUrl,
            "Where the build agent answers: https:// and the server's host name, nothing after.",
            "The address the portal calls the agent on — the one the server's web front serves, e.g. "
            + "https://build.example.com. The install script prints it when the server finishes setting up. Use "
            + "https:// from a real host name; plain http:// only on a private network the portal shares with the "
            + "host, where nothing else can listen in.",
            Why: "A wrong address leaves every claim at Build queued until the worker gives up on it as unreachable — "
                 + "no build runs, and the entrant is told the host could not be reached rather than that their "
                 + "work failed."),

        new("settings." + PreviewHostKeys.Token,
            "The token the agent was installed with. Stored encrypted and never shown again.",
            "The install script writes a random token to /etc/preview-agent/token on the build server and prints "
            + "it once at the end (in the instance's system log). Paste it here. To rotate it, write a new one to "
            + "that file on the server, restart the agent, and paste the new one here.",
            Why: "Anyone holding it can hand the host builds to run and read every build log. Treat it as a secret, "
                 + "and rotate it if the server is ever shared or the token ever pasted somewhere public."),

        new("settings." + PreviewHostKeys.BuildTimeoutMinutes,
            "How long one build may run before the host gives up on it. Fifteen minutes by default.",
            "A build that takes longer is stopped and marked Build failed with its log, and the host moves on "
            + "to the next. Raise it for work with slow builds — a large image pulled every time, a long compile "
            + "— and lower it if entrants' builds queue behind a runaway one. Between "
            + $"{PreviewHost.MinTimeoutMinutes} and {PreviewHost.MaxTimeoutMinutes} minutes."),

        new("settings." + PreviewHostKeys.RunUrl,
            "Where previews open: a domain of their own, each preview a name under it. Blank keeps the host to builds.",
            "A built milestone (and, after the deadline, the final version) can be run on the build host and opened "
            + "by the entrant, the opportunity's client and administrators. Each preview gets its own address under this "
            + "one — p-1a2b3c4d.previews.example.net under https://previews.example.net. Point a wildcard DNS record "
            + "(*.previews.example.net) at the build server and set the same name as PREVIEW_RUN_DOMAIN in "
            + "/etc/preview-agent/env there; Test build host says whether the two agree.",
            Why: "It must not sit under the portal's own domain. A preview runs an entrant's code: under the portal's "
                 + "domain it would be sent the portal's sign-in cookie in the two-site layout, and on any layout it "
                 + "could plant a cookie the portal then reads. The portal refuses such an address.",
            Example: "https://previews.example.net"),

        new("settings." + PreviewHostKeys.IdleMinutes,
            "A preview nobody has opened for this long is stopped. Thirty minutes by default.",
            "The host counts from the last request any viewer made to the preview. A stopped preview starts again "
            + "from the same button, in the time it takes the containers to start. Between "
            + $"{PreviewHost.MinIdleMinutes} and {PreviewHost.MaxIdleMinutes} minutes."),

        new("settings." + PreviewHostKeys.MaxRunning,
            "How many previews may run on the host at once. Three by default.",
            "Each running preview holds its app's memory on the build server; a start beyond this is refused with a "
            + "note to stop one or wait for the idle stop. Size it to the server: three typical web apps fit an "
            + $"8 GB host beside the builds. Between {PreviewHost.MinMaxRunning} and {PreviewHost.MaxMaxRunning}."),

        new("settings.opportunity.minAwardUsd",
            "Smallest award a client may offer for an opportunity.",
            "Opportunities below this figure cannot be published. A floor protects the platform's reputation with "
            + "freelancers, who decide whether to enter based on whether the award justifies the work.",
            Why: "Directly affects how many entrants an opportunity attracts. Set it too low and opportunities go unentered; "
                 + "too high and clients cannot list small jobs.",
            Example: "100"),

        new("settings.opportunity.defaultDurationDays",
            "Default number of days a new opportunity runs before its deadline.",
            "What a blank deadline becomes, counted from the opportunity's start date — the client can set their own "
            + "deadline instead, and the opportunity form shows the duration either way. At the deadline, entries "
            + "freeze and no further "
            + "commits count.",
            Why: "Too short and entrants cannot produce serious work; too long and clients lose interest before "
                 + "judging.",
            Example: "14"),

        new("settings.opportunity.reviewWindowDays",
            "Days the client has to review the winning code before paying the award.",
            "Starts when the client selects a winner. Reviewing the code before payment is central to how this "
            + "platform works — it is what makes entrants' effort worth risking.",
            Why: "An unreasonably short window pressures clients into paying without a real review; an open-ended "
                 + "one leaves winners waiting indefinitely for money they have earned.",
            Example: "7"),

        // ---------------------------------------------------------------
        // Legal
        // ---------------------------------------------------------------
        new("settings.legal.termsVersion",
            "Version number of the terms. Raising it makes everyone accept again.",
            "A whole number, 1 or more. Raise it whenever the terms change materially. Every signed-in user is "
            + "then asked to accept the new version on their next visit, and until they do, their account can "
            + "read, sign out or accept — nothing else of theirs saves. The acceptance is recorded on their "
            + "account against this number, with the time.",
            Why: "Raising it interrupts every user with an acceptance prompt, so do it for substantive changes and "
                 + "not for typo fixes. Lowering it does not undo acceptances already recorded.",
            Example: "2"),

        new("settings.legal.termsMarkdown",
            "The terms of service, in Markdown. Shown at signup, on the acceptance prompt, and at /terms.",
            "New accounts accept the current version at signup. Leave this empty and the portal has no terms: "
            + "nobody is asked to accept anything, and the /terms page says so.\n\n"
            + "If AI features send entrant code to an external provider, that processing must be disclosed here.",
            Why: "This is the agreement people are held to. Edits take effect immediately for new readers, but only "
                 + "count as re-accepted once you raise the version number above."),

        new("settings.legal.privacyMarkdown",
            "What the portal collects and what becomes of it, in Markdown. Linked from the join form and shown at /privacy.",
            "A separate document from the terms, and unversioned: nobody is asked to accept it again, because it "
            + "describes what this portal does rather than what a member agrees to. Say what is collected (email, "
            + "phone if given, profile, the code people push), who can see each of those, how long it is kept, and "
            + "who to write to about it.\n\n"
            + "Leave it empty and the portal has no policy: the join form links the terms alone and the /privacy "
            + "page says none is published. That is the honest default — a stock policy would describe somebody "
            + "else's portal.\n\n"
            + "If AI features send entrant code to an external provider, say so here as well as in the terms.",
            Why: "Several jurisdictions require this document before you collect an email address, and the join "
                 + "form links it beside the terms, so an empty one is visible to everybody who signs up."),

        // ---------------------------------------------------------------
        // Notifications
        // ---------------------------------------------------------------
        new("settings.notifications.digestHourUtc",
            "Hour of the day, in UTC, when digest email is sent. 0 is midnight.",
            "Always UTC, never local time — pick the hour that suits the majority of your users' timezone.",
            Why: "A badly chosen hour delivers digests overnight for most of your audience, and they are read late "
                 + "or not at all.",
            Example: "9"),

        // ---------------------------------------------------------------
        // Limits and maintenance
        // ---------------------------------------------------------------
        new("settings.limits.maxEntriesPerOpportunity",
            "Cap on entries per opportunity. Zero means unlimited, which is the intended default.",
            "Open entry is deliberate: any freelancer may enter any opportunity. A cap exists only for exceptional "
            + "cases, such as an opportunity attracting more entries than a client can realistically review.",
            Why: "A cap turns entry into a race and penalises freelancers in unfavourable timezones. Prefer the spam "
                 + "filter over a hard cap.",
            Example: "0"),

        new("settings.limits.captchaSiteKey",
            "Public Cloudflare Turnstile key, embedded in the signup and opportunity-entry forms.",
            "The provider is Cloudflare Turnstile — create a widget at dash.cloudflare.com and paste its site "
            + "key here and its secret below. The challenge appears on both forms only once both keys are set.\n\n"
            + "For testing, Cloudflare publishes dummy key pairs that always pass or always fail.",
            Why: "Without captcha configured, open entry is exposed to automated signups.",
            Example: "0x4AAAAAAA…"),

        new("settings.limits.captchaSecret",
            "Private Turnstile key used to verify challenges server-side. Stored encrypted.",
            "Verification fails closed: if Cloudflare is unreachable, signups and entries are refused rather "
            + "than let through unverified.",
            Why: "If this does not match the site key above, every captcha verification fails and nobody can sign up."),

        new("settings.limits.maxUploadMb",
            "Size ceiling for a single uploaded file — brief attachments today, submissions later.",
            "Checked when an upload is requested and again against the real object once it lands, so a client "
            + "cannot promise 2 MB and deliver 2 GB.\n\nRaising it raises your storage bill in step; on R2 the "
            + "egress is free but the stored bytes are not.",
            Why: "Every uploaded byte is stored for the life of the opportunity and served to every entrant who "
                 + "downloads the brief. The default suits documents and design masters; video walkthroughs "
                 + "deserve a link, not an upload.",
            Example: "25"),

        new("settings.limits.maintenanceMode",
            "Takes the portal offline for everyone except administrators.",
            "Public pages show a maintenance notice. Administrators can still sign in and use the admin screens, so "
            + "you can verify a deployment before letting people back in.\n\n"
            + "Background jobs continue to run — webhooks are still received and recorded.",
            Why: "Entrants working against a deadline are locked out while this is on. If an opportunity deadline falls "
                 + "during maintenance, extend it."),

        new("settings.limits.activityRetentionDays",
            "How many days the activity log keeps. 0 keeps everything.",
            "Admin → Activity records every page opened and every action taken — by members and by visitors "
            + "who never signed in — and every call the portal made to another service, with what it sent "
            + "and what came back. Once an hour, rows older than this many days are deleted.\n\n"
            + "Ninety days is enough to answer \"what happened to this account\" without keeping a year of "
            + "everybody's browsing. Zero turns the sweep off; the table then grows for as long as the portal "
            + "runs.",
            Why: "The log holds IP addresses and browsing, which are personal data wherever you operate. Keep "
                 + "it as short as your questions need, and say in the privacy policy that it exists."),

        new("settings.limits.inboxRetentionDays",
            "How many days a member's notifications stay behind the bell. 0 keeps them all.",
            "The bell in the header opens a member's inbox: every email the portal sent them about something "
            + "they are party to, and every opportunity opening or winner announced that they asked to hear about, "
            + "each a line that a click follows to the page it concerns. Once an hour, lines older than this "
            + "many days are deleted, read or unread.\n\n"
            + "Ninety days keeps a season's worth without the inbox growing for as long as the account lasts. "
            + "Zero turns the sweep off. The emails themselves are unaffected; this is only what the bell shows.",
            Why: "An unread line about an opportunity that closed months ago is noise, and a long inbox makes the "
                 + "new lines harder to find. Shorten it on a busy portal; lengthen it where members visit rarely."),

        new("settings.limits.slowQueryMs",
            "Database commands this slow or slower are logged and listed under Operations. 0 turns it off.",
            "Each one is written to the API's log as a warning, with its SQL and the request it served, and "
            + "tallied under Admin → Operations → Slow queries, one row per query in the code. The SQL carries "
            + "placeholders where the values went, never a member's data.\n\n"
            + "A saved value is in force within a few seconds, with no restart, and the Operations tally starts "
            + "again from that moment so every row it shows was counted under the same threshold. To find the "
            + "queries worth tuning, lower it to 100 for a day of ordinary use, read Operations, then put it back.",
            Why: "Too low and ordinary queries bury the slow ones — and the log grows with every page view. Too high "
                 + "and a query that makes every page drag never shows. 500 ms is about where a person notices.",
            Example: "100"),

        new("settings.limits.slowQueryBackground",
            "On, the slow-query log also counts the portal's own background work. Off, only what a request ran.",
            "Background work is what the API does on its own schedule rather than for a page somebody opened: "
            + "sending the email and push outboxes, setting up GitHub repositories, AI drafts, writing and pruning "
            + "the activity log, the daily merit snapshot. Its commands serve no request, so the log and "
            + "Admin → Operations → Slow queries say they ran during background work.\n\n"
            + "Off, those commands are neither logged nor tallied, however slow, and Operations lists only the "
            + "queries a person waited on. A change is in force within a few seconds, with no restart, and the "
            + "Operations tally starts again from that moment.",
            Why: "A worker's batch can be slow with nobody waiting for it, and a busy outbox can fill the list and "
                 + "push out the queries that make pages drag. Leave it on to see everything; turn it off while you "
                 + "look for what slows pages down."),

        new("settings.limits.publicCacheSeconds",
            "How many seconds the public lists are reused before the database is asked again. 0 turns it off.",
            "The opportunity feed, the figures above it, the leaderboard and the Talent page show every visitor the "
            + "same opportunities and the same members. For this many seconds the API answers them from its last read "
            + "rather than asking the database again, so a busy landing page costs one read per interval instead "
            + "of one per visitor. Visitors who arrive while a read is under way wait for that one read.\n\n"
            + "What depends on the visitor is still worked out for each of them: the Recommended matches, whether "
            + "entry is still open, the board's tabs and the Talent filters. Publishing, cancelling or awarding a "
            + "opportunity, marking an award paid, and locking or erasing an account empty it at once, so whoever made "
            + "the change sees it on the next page. Anything else, such as a new entrant, an application or a "
            + "rating, shows within this many seconds. A saved value is in force on the next request, with no "
            + "restart.",
            Why: "Longer takes more load off the database and lets the public lists fall further behind. Ten "
                 + "seconds is too short for a visitor to notice, and during a rush it turns hundreds of identical "
                 + "reads into one. Turn it off only to rule it out while looking into a list that looks wrong.",
            Example: "10"),

        // ---------------------------------------------------------------
        // Redis — read once at startup, so every change waits for a restart
        // ---------------------------------------------------------------
        new("settings.redis.enabled",
            "Whether the API uses Redis. Off, one API process needs nothing; on, several API processes share one Redis.",
            "Redis carries what has to cross between API processes: a settings save, a clear of the public lists' "
            + "cache, a wake for the background workers, and the live board's rooms. One API process keeps all of "
            + "that in its own memory and needs no Redis at all, which is why this is off by default and a portal "
            + "that never opens this group loses nothing.\n\n"
            + "Read once, when the API starts. A change here waits for the next restart of the API, and the group "
            + "shows Restart pending until then. The deployment can pin it instead, as WP_REDIS_ENABLED; the older "
            + "REDIS_URL, which the compose file sets, switches it on with that address.",
            "Switched on with no address, or one that does not answer, the API runs as if this were off and says so "
            + "in its log. While a Redis it was started with is down, pages still load but no opportunity board is live.",
            "off"),
        new("settings.redis.url",
            "The Redis server, as host:port, with any StackExchange.Redis option after a comma.",
            "One server, or several separated by commas for a cluster, then any option the StackExchange.Redis "
            + "configuration string takes (ssl=true, password=..., defaultDatabase=1). A redis:// address works "
            + "too. Every API process that shares the portal must name the same Redis, and none of the portal's "
            + "data is stored in it: it carries messages between processes and nothing else.\n\n"
            + "Read once, when the API starts, like the switch above.",
            "A password typed here is stored as it is and shown on this screen to any administrator. Where that "
            + "must not be, pin the address from the environment instead (WP_REDIS_URL), and the field shows as locked.",
            "cache:6379"),

        // ---------------------------------------------------------------
        // JWT — read once at startup, so every change waits for a restart
        // ---------------------------------------------------------------
        new("settings.jwt.issuer",
            "Who a sign-in token says issued it. A token naming any other issuer is refused.",
            "Written into every token the portal signs and checked on every request. This API is the only party "
            + "that issues or checks the portal's tokens, so the name matters only to an app that reads it — "
            + "change it when one expects a particular value.\n\n"
            + "A saved change takes effect when the API restarts.",
            Why: "Changing it signs everybody out at that restart: every token already issued names the old issuer "
                 + "and is refused.",
            Example: "WinnersPortal"),

        new("settings.jwt.audience",
            "Who a sign-in token is meant for. A token naming any other audience is refused.",
            "Written into every token the portal signs and checked on every request, beside the issuer. Leave it "
            + "as it is unless an app that reads the token expects a particular value.\n\n"
            + "A saved change takes effect when the API restarts.",
            Why: "Changing it signs everybody out at that restart: every token already issued names the old audience "
                 + "and is refused.",
            Example: "WinnersPortal"),

        new("settings.jwt.signingKey",
            "The secret every sign-in token is signed with. Stored encrypted; replaced, never removed.",
            "The portal signs with HMAC-SHA256, so the key is a shared secret: base64 of at least 32 random bytes. "
            + "Generate makes one in your browser. Paste your own only if a random generator made it — never a "
            + "password or a phrase.\n\n"
            + "The first start stores a key by itself (the one an earlier release kept as jwt-signing.key in the "
            + "keys folder, when that file is there), so leaving this blank keeps signing exactly as it does now. "
            + "A new key takes effect when the API restarts.",
            Why: "Whoever holds this key can make a token for any account, administrators included. Replacing it "
                 + "signs everybody out at the next restart — which is the point when it may have leaked. It is "
                 + "encrypted with the data-protection keys, so losing that folder loses the key too, and everybody "
                 + "signs in again."),

        new("settings.jwt.accessTokenMinutes",
            "How long an app's access token works before the app renews it. 5 to 1440 minutes.",
            "Applies to apps that sign in for a token pair rather than through the browser. When the access token "
            + "lapses, the app trades its refresh token for a new pair, so a shorter lifetime costs the app one "
            + "request, not another sign-in. Browser sessions are not affected: they last about 7 days, renewed "
            + "while in use.\n\n"
            + "A saved change applies to tokens issued after the API restarts.",
            Why: "Every request also checks the account, so a lock, a password change or revoking every session ends "
                 + "a token at once whatever its lifetime. The lifetime bounds how long a copied token keeps working "
                 + "while nothing about its account changes.",
            Example: "60"),

        new("settings.jwt.refreshTokenDays",
            "How long an app stays signed in while unused. 1 to 365 days.",
            "Every refresh issues a new refresh token with a fresh lifetime, so an app used at least this often "
            + "never asks for the password again, and one left longer signs in again.\n\n"
            + "A saved change applies to tokens issued after the API restarts; ones already issued keep the "
            + "lifetime they were given.",
            Why: "A lost or stolen device stays signed in this long unless its account is locked, its password "
                 + "changed, or every session revoked.",
            Example: "30"),

        // ---------------------------------------------------------------
        // Integrations and feature flags
        // ---------------------------------------------------------------
        new("settings.features.publicMilestoneBoard",
            "Makes entrants' milestone boards visible to signed-out visitors.",
            "Shows progress — milestones completed, commit activity — on public opportunity pages. It does not expose "
            + "any entrant's code, which stays private until an award is transferred.\n\n"
            + "Public progress is good marketing for the platform and good social proof for entrants, but some "
            + "entrants would rather not have their pace visible to competitors.",
            Why: "Turning this on retroactively makes existing opportunities' boards public. Consider whether your terms "
                 + "told entrants that would happen."),

        // ---------------------------------------------------------------
        // Phase two — forms, not settings. Same coverage rule: these fields
        // cost money, feed an integration, or cannot be undone.
        // ---------------------------------------------------------------
        new("opportunity.awardAmount",
            "The fixed amount the winner is paid. Entrants commit real days against this number.",
            "There is no bidding on this platform: you set one award, freelancers decide whether the work is worth it, "
            + "and the winner is paid exactly this amount after you review their code.\n\n"
            + "Set it for the work you are asking, not the minimum the form accepts — the award is the only thing "
            + "competing for entrants' time against every other open opportunity.",
            Why: "This is a public commitment. It is shown on every card in the feed, it cannot be lowered after "
                 + "publishing, and your payment record on it follows your account.",
            Example: "750"),

        new("opportunity.startsAt",
            "The day the work starts. Information for the opportunity duration — it opens and closes nothing.",
            "Entry opens the moment you publish, whatever day this says. That lets you take entrants, look them "
            + "over and close entry with the last joining date before the work begins, then have the build start "
            + "here. It is today unless you change it.\n\n"
            + "The opportunity runs from the start to the deadline, and the form shows how long. A blank deadline counts "
            + "the portal's default duration from the start.",
            Why: "Like the deadline, it is fixed at publish, because entrants plan their weeks around it. It has to "
                 + "come before the deadline with at least 24 hours between them; a day that has gone by when you "
                 + "publish counts as the day you publish.",
            Example: "Next Monday, with the last joining date on the Friday before"),

        new("opportunity.deadline",
            "When every entrant's work freezes for your review.",
            "Until the deadline, entrants push code. At the deadline the boards freeze and you get read access to "
            + "every entry's repository to pick a winner.\n\n"
            + "Leave it blank to use the portal default, counted from the start date. Give real projects real time — a deadline that is too tight "
            + "mostly filters out the careful entrants.",
            Why: "The deadline cannot be moved after publishing: entrants plan their week around it, and quietly "
                 + "extending it would hand latecomers an advantage over people who scoped to the original date."),

        new("opportunity.entryClose",
            "The last joining date. After it nobody new can enter, and the entrants already in build on.",
            "By default entry stays open right up to the deadline, which suits most opportunities. Set a date here to "
            + "shut the door earlier — \"join in the first week, build for three\".\n\n"
            + "Closing entry early trades reach for certainty. A settled field means every entrant knows what they "
            + "are up against and nobody arrives late with a head start from reading the board; it also means the "
            + "strong entrant who finds your brief in week two cannot enter at all.",
            Why: "Like the deadline, it is fixed at publish. It can never be later than the deadline but may come before the start date, so the field is settled before the work begins; an opportunity "
                 + "whose entry has closed still shows publicly — it is open and building, not finished.",
            Example: "One week after publishing, for a four-week deadline"),

        new("opportunity.milestoneDates",
            "Optional date per milestone. With one, the board can tell a claim on time from a claim behind.",
            "A milestone with no date shows only done or not done — which is how every board worked before dates "
            + "existed, and is still the right answer for exploratory work.\n\n"
            + "Give one a date and three things become visible: a claim on or before it reads as on time, a claim "
            + "after it reads as late, and a milestone still unclaimed once its date passes reads as overdue. The "
            + "board sorts on that, so entrants keeping to your dates rise to the top of it.\n\n"
            + "Dates have to fall on or before the deadline, and cannot already be in the past when you publish.",
            Why: "Entrants scope their weeks against these dates and they are fixed at publish, like everything "
                 + "else in the brief. Dates tighter than the work needs will read as a field of late entrants "
                 + "rather than as an ambitious schedule."),

        new("opportunity.milestoneShares",
            "Optional share of the work per milestone, in percent. State it on every milestone or on none.",
            "A share tells entrants which steps carry the work: a checklist of four with 10, 20, 30 and 40 "
            + "says the last step is the one to budget for. The page shows each milestone's share beside it.\n\n"
            + "Leave every share blank and the checklist reads as it always did. Fill one in and publish "
            + "expects all of them, adding up to 100 — the form keeps a running total and can split the "
            + "whole evenly for you.\n\n"
            + "Shares describe the work; they do not weight the board. Standing counts milestones met and "
            + "met on time, share or no share.",
            Why: "Frozen at publish with the checklist. A share that says a step is a tenth of the work, "
                 + "when it is half, is a promise entrants plan their weeks on.",
            Example: "25"),

        new("opportunity.requirements",
            "The technical requirements, one row each: what the constraint is, and what it asks for.",
            "A small table for the terms a brief tends to bury — the language and version, the frameworks, "
            + "the data, how the result is served, how it is evaluated. Each row is a name and what it asks "
            + "for: \"Language\" and \"Python 3.11+\".\n\n"
            + "The table is shown on its own tab beside the required skills, so an entrant can check the "
            + "constraints in a glance before reading the brief. It is not the door: nobody is refused for "
            + "it, and the brief remains the place to say why each constraint matters.",
            Why: "Frozen at publish with the brief. A requirement added afterwards is one entrants did not "
                 + "agree to, and one left out is one they will not build to.",
            Example: "Serving — Inference API with a latency budget"),

        new("opportunity.rubric",
            "How the work is scored: points per criterion, adding up to the rubric's total.",
            "Name each thing the work is judged on and how many points it carries; the page adds them up "
            + "and tells entrants the total, and the sentence above the table says the highest composite "
            + "score wins. The form offers a standard five-line rubric — functionality, craft, UX, "
            + "timeliness, documentation — to start from.\n\n"
            + "The rubric is what you promised to choose by. The choice itself is still yours, made after "
            + "reading the work, with the AI review as one input.",
            Why: "Frozen at publish. Entrants decide where to spend their time by the points — a rubric that "
                 + "gives documentation a tenth will get a tenth of their attention on it.",
            Example: "35 pts — Functionality — all requirements met and working end-to-end"),

        new("opportunity.standing",
            "How each entrant is tracking against your milestone dates — on time, late, overdue.",
            "These counts are the largest single part of the standing score the board is ordered by: a "
            + "milestone met on time is worth twice one met late, and one left overdue is worth nothing. The "
            + "rest of the score reads the same board — milestones claimed, whether the entrant keeps working, "
            + "what the work itself shows — and then their record elsewhere and their portfolio.\n\n"
            + "This is a reading aid, not a verdict. Nothing here picks a winner, caps an award, or removes "
            + "anybody — you choose the winner by reading the code, and this only says whose week to read first.",
            Why: "It is public wherever the board is, so an entrant who slips sees the same row you do. That is "
                 + "the pressure the dates are for, and the reason to set dates you would defend."),

        new("opportunity.standingScore",
            "One number, out of 100, for where each entrant stands on this opportunity — best first, every point explained.",
            "Half of it is what they are doing here: milestones met on time (20), milestones claimed (10), "
            + "whether they keep working (10), and what the work shows where the portal can read it — tests, a "
            + "README and CI in a repository, or files and a document on an upload opportunity (10). A quarter is "
            + "their record elsewhere: opportunities finished (10), the win ratio (10), ratings from clients (5). The "
            + "last quarter is what they wrote: the portfolio (15) and the skills on their profile that this "
            + "brief actually names (10).\n\n"
            + "A part the portal cannot read yet — a repository nobody has pushed to, an opportunity whose dated "
            + "milestones have not come due — is left out rather than scored as a miss, so rows are compared on "
            + "what is known about both. The word beside the number reads this opportunity alone: on track, "
            + "behind, at risk, or too early to say.",
            Why: "It orders the board and sits beside the remove button, and it decides nothing — you keep or "
                 + "remove an entrant, and you pick the winner by reading the work. An entrant sees their own "
                 + "breakdown and the one step that would move them up; you see everyone's."),

        new("opportunity.aiStandingNotes",
            "AI-drafted notes on the board — what each row's numbers show, and what to open before deciding who stays.",
            "The portal computes every standing score and the order first; the model is given those numbers "
            + "and their explanations and asked to put each row into two or three plain sentences, naming the "
            + "concrete thing to check — a milestone, the last activity, the files or the repository. It never "
            + "scores, re-orders or compares entrants, and it is told not to recommend keeping or removing "
            + "anyone.\n\n"
            + "Drafted on request and cached: asking again re-reads the board only if something changed. Nothing "
            + "from inside a repository is sent — the notes are written from the board's own facts.",
            Why: "Thirty rows of numbers are hard to hold in your head at once; thirty short notes are not. The "
                 + "decision they support — keep or remove — is the harshest one a client makes on this portal, "
                 + "so the notes point at what to open rather than telling you what to conclude."),

        new("opportunity.brief",
            "The specification entrants build against — and the document disputes are judged by.",
            "Write what you need, what done means, and any hard constraints (stack, hosting, integrations). Markdown "
            + "is supported: headings and lists make a brief much easier to build against.\n\n"
            + "Ambiguity is the expensive failure here: almost every disputed opportunity traces back to a brief that "
            + "meant different things to the client and the entrant.",
            Why: "Once published, the brief cannot be edited — it is the fixed thing every entrant committed their "
                 + "time to. Say it now or run another opportunity for it later."),

        new("opportunity.category",
            "What kind of work this is — how entrants browse for it, and half of what tells them it is theirs.",
            "Pick the category and, where one fits, the subcategory. The browse page filters by both, so a "
            + "opportunity with the wrong one sits where nobody who does that work is looking. The form reads your "
            + "title and brief against a word list for each kind and suggests one as you type — a suggestion, "
            + "from the words alone; you are the one who knows what you are asking for.\n\n"
            + "For a freelancer with the Recommended switch on, an opportunity in a category their skills fall under "
            + "reads as a fit; one outside it is still open to them, and says so.",
            Why: "Required to publish. A category filter over a list that half the opportunities skipped would be "
                 + "worse than no filter, so none may skip it."),

        new("opportunity.requiredSkills",
            "Skills an entrant's profile must list to enter. Leave it empty to ask for none.",
            "Each one is matched against the skills on a freelancer's profile, case and spacing aside — "
            + "\"react native\" satisfies \"React Native\". A profile short of any of them is refused at the "
            + "door, with the missing skill named, and the browse page shows the opportunity in red to that person "
            + "before they open it. The form offers the skills your brief mentions and, once you have picked "
            + "what kind of work it is, the skills that kind of work usually needs — the subcategory's own "
            + "first, when you have picked one; nothing is added until you press one.\n\n"
            + "Skills are self-reported, so this is a filter on who says they can, not proof that they can — "
            + "the code they hand in is the proof. Keep the list to what the work actually needs: a brief "
            + "that requires nine technologies is asking nine questions nobody has to answer yet.",
            Why: "This is a term of the deal, frozen at publish with the award: an entrant who was let in "
                 + "cannot later be told their profile was never good enough."),

        new("opportunity.minMerit",
            "The merit score an entrant needs to enter, out of 100. Zero — the default — is no minimum.",
            "The merit score is the portal's own reading of a freelancer: thirty points for what they wrote "
            + "about themselves, seventy for what they did here — opportunities entered, milestones met on time, "
            + "awards won, ratings from clients. A floor of 25 asks for a written profile or one finished "
            + "opportunity; 50 asks for a record here; 75 asks for a proven one. Somebody short of it cannot enter "
            + "and is told the number they have and the number they need.\n\n"
            + "A high floor on a portal that is still young shuts out nearly everyone, including the person "
            + "who would have won. Set it for the risk in the work, not as a proxy for quality.",
            Why: "Frozen at publish, like the award. It is the one term an entrant cannot argue with at the "
                 + "door and cannot fix in an afternoon, so it should be the one you thought hardest about.",
            Example: "25"),

        new("opportunity.match",
            "How well you match an opportunity \u2014 the ring on every card, and the assessment on its page.",
            "Four lines, each out of 100, averaged with the skills counting double: your expertise in the "
            + "required skills (the level and years your profile gives each, nought for one not listed), "
            + "relevant project history (past work filed under this kind of work or mentioning a required "
            + "skill), your on-time delivery record here, and your availability against the timeline (when "
            + "you can start, hours a week, and how many opportunities you are already in). A line the portal "
            + "cannot read yet \u2014 no dated milestone has come due, nothing said about availability \u2014 "
            + "is left out rather than scored as nought. Strong fit is 80 and up, Good fit 60, Moderate "
            + "below that, and Out of category is where the portal has a reading of what kinds of work "
            + "your skills cover and this is not one of them.\n\n"
            + "It is not the door. A profile one skill short of five can read Strong fit and still be "
            + "turned away \u2014 the door is every required skill and the merit floor, and the card's own "
            + "line says which is missing. The pulse at the other corner is the opportunity's clock: Live, "
            + "Ending within three days of its joining date, Ended once it has passed or the opportunity is over.",
            Why: "A colour says yes or no; a number says how near, and the lines say which way. The "
                 + "weakest of them is the one sentence of advice on the entry box, and the profile is "
                 + "where it moves."),
        new("opportunity.milestones",
            "The checklist that becomes every entrant's progress board.",
            "Break the work into the steps you want to see finished, in order. Each entrant gets this list as their "
            + "board, and (from phase three) commits and checkpoints stamp progress against it automatically.\n\n"
            + "Five to ten concrete milestones beat two vague ones: they are how you will tell a stalled entry from "
            + "a healthy one at a glance.",
            Why: "Progress tracking measures against this list, so a missing step is invisible to it. Like the brief, "
                 + "the list is fixed at publish."),

        new("profile.merit",
            "One number for how much a client can lean on what they are reading — 30 written, 70 earned.",
            "The portfolio half is self-reported and caps at 30, because anyone can type anything and a "
            + "score you could max out in an afternoon would be worth nothing. The other 70 is earned "
            + "here: opportunities entered, milestones met on time, awards won, ratings from the other side "
            + "of finished deals.\n\n"
            + "Every point has a line in the breakdown saying what it was for, and both halves are "
            + "visible to anyone who can see your profile. Volume flattens on purpose — entering "
            + "everything is not a strategy, and one five-star rating is not a reputation.",
            Why: "It decides nothing. It does not gate entry, cap an award or pick a winner — the client "
                 + "picks the winner by reading the code. Its written half is one part, and the smallest, of "
                 + "the standing score an opportunity board is ordered by. Treat it as a reading aid, and be "
                 + "suspicious of anyone who treats it as a verdict."),

        new("profile.about",
            "A picture, a professional title and an About — what a client reads before they open your code.",
            "The title is one line, and it appears under your name wherever you do. About You is where to "
            + "say what you build, who for, and how you work. The picture sits beside your name everywhere "
            + "it is shown.\n\n"
            + "Specific beats broad: \"invoicing and GST reporting for wholesalers\" tells a client "
            + "something \"full-stack developer\" does not."),

        new("profile.photo",
            "A clear picture of your face, shown beside your name across the portal.",
            "Anyone who sees your name sees this: the header, entrant lists, boards, your profile page. Choose "
            + "one a client would recognise you from on a call — face clear, decent light, the way you would "
            + "turn up to work.\n\n"
            + "The picture is shrunk to a small square in your browser before it is sent, so a phone photo is "
            + "fine; JPEG, PNG or WebP. Remove it and the portal shows your initial instead."),

        new("profile.aiSummary",
            "Asks the model for a first draft of your About, written from the rest of this form.",
            "Reads your title, skills, languages and past work as they stand on this form — no need to save "
            + "first — and drafts a short summary in your own voice. Use it as it is, edit it, or keep what you "
            + "wrote; nothing is applied until you choose.\n\n"
            + "One call per press, and the button waits until you have changed something before it will read "
            + "again. It only knows what is on the form: fill in a title and a few skills first, and it says so "
            + "when there is too little to go on. Your email and payment details are never sent."),

        new("profile.whereYouWork",
            "Location, time zone and years of experience — the context everything else is read in.",
            "A client reading \"20 hours a week\" needs to know which hours: the time zone is what "
            + "makes a working window and a call time mean something eight hours away. The location "
            + "is as you write it, never checked against a list.\n\n"
            + "The portal never asks for a date of birth, a government identity document or a phone "
            + "number, because it has no use for them and cannot leak what it does not hold."),

        new("profile.availability",
            "Whether you can take a project on now — and how much of a week, for how long, on what terms.",
            "A client reading a three-week deadline needs to know whether you have three weeks. "
            + "Current availability is the status: available now, within a week, within two, or not "
            + "taking anything on. Weekly capacity is the hours; it counts towards your merit score "
            + "with your location and years of experience, because together they are what make a "
            + "deadline real.\n\n"
            + "The rest is preference. Short-, medium- or long-term work; competitive fixed price, "
            + "which is what an opportunity here is, direct fixed price, or hourly. A working window — "
            + "\"09:00 – 18:00\" in your own time zone — tells a client when a reply is likely.\n\n"
            + "None of it is a gate. An opportunity never checks these, and preferring long-term work does "
            + "not keep you out of a two-week one. Keep the status current: \"available now\" is "
            + "something a client acts on.",
            Why: "Matching on whether you can actually start is worth more than matching on anything "
                 + "else. The best-fitting member who cannot begin for a month is not a fit for a "
                 + "opportunity that closes in three weeks."),

        new("profile.categories",
            "The kinds of work you do, in the same words an opportunity is filed under.",
            "One primary kind and up to three more. It is the same taxonomy every opportunity uses, which "
            + "is the point: a brief that says \"mobile apps\" and a member who says \"mobile apps\" are "
            + "saying the same thing, and the portal can put the two in front of each other.\n\n"
            + "It also decides which skills you are offered below. Pick the kinds you would take a "
            + "opportunity in tomorrow — claiming all of them claims none of them.",
            Why: "Nothing here is a filter you have to pass. An opportunity in a category you did not name "
                 + "is still yours to enter, and the merit floor and required skills are the only gates "
                 + "there are."),

        new("profile.skills",
            "The skills you would be hired for. Six named well say more than twenty listed.",
            "Each carries a level — beginner, intermediate, advanced, expert — and how long you have "
            + "used it. Beginner is used it and still learning it; intermediate ships production work "
            + "with it; advanced ships the hard parts unsupervised; expert is the person others ask.\n\n"
            + "The merit score counts the first six, deliberately: a keyword dump is not a portfolio, "
            + "and rewarding one would fill the portal with them. The names under the list are "
            + "suggestions for the kinds of work you chose — this portal's spelling of them, so one "
            + "skill is one name across every profile."),

        new("profile.workType",
            "Whether freelancing is your whole week, part of it, your own business, or a team's.",
            "A client reading \"20 hours a week\" is trying to work out what happens when the deadline "
            + "moves. Part-time beside a job is a different answer from full-time, and an agency member "
            + "is a different answer again — somebody else may be doing the work beside you.\n\n"
            + "None of the four is worth more than the others here, and no opportunity is closed to any of "
            + "them."),

        new("profile.languages",
            "The languages you can work in, and how well — a client picking a call language reads this.",
            "Each one carries a level, because a list of names does not answer the question anybody is "
            + "actually asking. Beginner is a few phrases; conversational holds a call given patience; "
            + "professional is meetings, written specs and code review; fluent is no effort at all; "
            + "native is the one you grew up with.\n\n"
            + "Claim the level you would be comfortable being tested on in the first stand-up. A "
            + "language listed twice is refused rather than saved — two levels for one language is two "
            + "different claims, and a client cannot tell which to believe.",
            Why: "Overstating one here costs more than leaving it off. A client who picks an opportunity, a "
                 + "brief and a call schedule around \u201Cfluent English\u201D and then cannot run the call "
                 + "has lost the week, and it goes on your record rather than theirs."),

        new("profile.payment",
            "How you want an award paid. Only administrators and a client who has made you a winner can see it.",
            "The portal never touches the money: a client pays the winner directly and then marks the "
            + "award paid here. So this is the answer to \u201Cwhere do I send it\u201D, written once instead "
            + "of over email every time you win.\n\n"
            + "Pick a method and it asks for exactly what that method needs \u2014 a bank transfer wants an "
            + "account title and a number, a crypto address is useless without its network, a "
            + "remittance needs the name printed on the ID you will show at the counter. List more than "
            + "one if you can be paid more than one way; the client picks whichever costs them least.\n\n"
            + "Who can read it: you, the portal\u2019s administrators, and a client once they have announced "
            + "you as the winner of one of their opportunities \u2014 from then on, paid or not. Any other client "
            + "reads a line saying the details appear once they announce you, never whether you saved any; "
            + "other freelancers cannot read it, and are not told there is anything to see. The details are "
            + "encrypted before they are "
            + "stored, so a copy of the database is not a list of account numbers.",
            Why: "A wrong digit is a payment that bounces or, worse, one that arrives somewhere else. "
                 + "Nobody at the portal checks these against anything \u2014 they are yours, typed by you, "
                 + "and copied by a client who has no way to know they are wrong."),

        new("profile.strength",
            "The seven steps of joining, ticked off your saved profile \u2014 not the merit score.",
            "One tick per step: a confirmed contact, a title and an About, a kind of work with at least one "
            + "skill, at least one project, an availability, a way to be paid. Six of seven reads 86%. A tick "
            + "is the least a section is for, not the most it can hold \u2014 one project ticks Portfolio, and "
            + "the merit breakdown below is where more of them count.\n\n"
            + "Only you see this. It reads your saved profile, so a change on the Edit tab shows here once it "
            + "is saved.",
            Why: "A checklist and a score answer different questions. The score is what a client leans on and "
                 + "moves slowly on purpose; this is what is left to do, and it should reach the end."),

        new("profile.review",
            "What your profile is strongest for, and what would make more open opportunities yours to enter.",
            "The portal reads your saved profile against every open opportunity\u2019s door \u2014 the merit floor "
            + "and the required skills \u2014 and works out what each change would open: a skill the opportunities "
            + "ask for, or the merit points a title, an About, more projects or a GitHub link are worth, where "
            + "those reach a floor. The figure on each line is that arithmetic \u2014 how many more of every "
            + "hundred open opportunities you could enter once it is done \u2014 and the three best are shown. The "
            + "model only puts them into words, and says what your profile is strongest for.\n\n"
            + "It is re-read when your profile or the open opportunities change. Nothing here is applied for you, "
            + "and nobody else is shown it.",
            Why: "You cannot see what the open opportunities are asking for from your own page, and the commonest gap "
                 + "between a profile and an opportunity is one skill nobody listed."),
        new("profile.welcome",
            "Where you stand on the day you finish joining \u2014 and any day you come back to look.",
            "The opportunities are the open opportunities the portal recommends to you: ones whose door your "
            + "profile opens \u2014 the merit floor and the required skills \u2014 and whose kind of work is yours. "
            + "The strongest is the recommended one that asks for the most of your skills, the larger award "
            + "deciding a tie; where nothing open is yours to enter yet, the review on your profile\u2019s "
            + "Preview tab says what would open one.\n\n"
            + "Where one of your own projects fits the strongest opportunity, filed under its kind of work or "
            + "mentioning a skill it asks for, its card ends with that project and what came of it: the first "
            + "such project on your profile with an outcome written.\n\n"
            + "Nothing here is fixed. Every number is read off your saved profile and the opportunities open "
            + "today, so the screen a week later says that week\u2019s truth.",
            Why: "A member who has just finished joining should see what joining bought \u2014 and the same "
                 + "numbers on any later visit, so the first ones were never a welcome-only flattery."),
        new("profile.rank",
            "Your merit score\u2019s place among every freelancer here, and among those doing your kind of work.",
            "One more than the number of freelancers with a higher score, so equal scores share a rank and "
            + "#1 is the top of the portal. The second rank counts only members whose primary category is "
            + "yours. Both are read fresh each time and move as scores do \u2014 yours and everybody else\u2019s.",
            Why: "A score out of 100 says how far a client can lean on you; a rank says how that compares, "
                 + "which is what somebody new actually wants to know."),
        new("leaderboard.tabs",
            "Five boards over the same members: all, a region, a kind of work, new joiners, and those a client scored.",
            "Global lists every freelancer with a score. Regional places members by the time zone on their "
            + "profile \u2014 the one structured place a member gives \u2014 into Africa, the Americas, Asia, Europe and "
            + "Oceania; a member with no zone is on no region. Category lists whoever named that kind of work as "
            + "their primary or one of their secondary ones. Rising Talent is whoever joined in the last ninety "
            + "days. Performance Leaders are the members a client has passed a verdict on \u2014 an opportunity won or a "
            + "rating given \u2014 so it lists proven delivery rather than a well-written profile.\n\n"
            + "Under the tabs, the board\u2019s top three by merit score stand as three cards \u2014 gold, silver, "
            + "bronze \u2014 whatever orders the table below them.\n\n"
            + "A member is on the board once their account is confirmed and their score is above nought; a "
            + "locked or deleted account is not. The pickers offer only regions and kinds of work with somebody "
            + "on them, and open on the fullest.",
            Why: "The board decides nothing. It ranks what the portal recorded, and the profile a row opens is "
                 + "where a client reads the work behind the figures."),
        new("leaderboard.rankBy",
            "Which figure orders the board. Equal figures share a rank, and the next takes the place after them.",
            "MeritScore is the number out of 100 \u2014 30 for what a member wrote, 70 for what they earned here. "
            + "Rating is the star average from clients after paid awards; among equals, more ratings come first. "
            + "Delivery is dated milestones met on time as a share of those faced; among equals, more milestones "
            + "come first. Wins is opportunities won.\n\n"
            + "A figure the portal has not read \u2014 no rating yet, no dated milestone faced \u2014 shows a dash and "
            + "sorts below every real one, unranked, rather than as a nought that would tie with a member who scored nought. "
            + "Ranks are competition ranks: one more than the number with a higher figure, so two at 4.8 are both "
            + "#1 and the next is #3.",
            Why: "One figure per view, so a rank means one thing. Whichever is chosen, the other three stay in "
                 + "their columns."),
        new("leaderboard.delivery",
            "Dated milestones claimed on or before their date, as a share of the dated milestones faced.",
            "Counted across every active entry, the same way the merit score\u2019s punctuality points count. A "
            + "milestone with no date cannot be met late or on time, so it is not in the count, and a member who "
            + "has faced no dated milestone yet shows a dash rather than a nought.",
            Why: "Reliability is the figure a client asks for first, and it is the one a profile cannot claim: "
                 + "every point of it was stamped by a webhook or an upload against a date the client set."),
        new("leaderboard.trend",
            "How the merit score moved over up to thirty days: green up, red down, a dash for no change.",
            "The portal writes every freelancer\u2019s score down once a day. The arrow compares today\u2019s live "
            + "score with the oldest recorded in the last thirty days: on a member\u2019s first day on the board it "
            + "reads new, on the second it is the change since the day before, and after a month it is a "
            + "month\u2019s movement.\n\n"
            + "A score that fell is a fact about the record, not a mark against anybody: a rating edited down "
            + "or an entry withdrawn moves it, and so does a milestone claimed late.",
            Why: "A rank says where somebody stands; the trend says which way they are going, which is what a "
                 + "client choosing between two similar profiles actually wants to know."),
        new("talent.panels",
            "Four panels over the leaderboard’s members: highest scores, fastest climbing, each kind of work’s leader, most reliable.",
            "Top Talent is the four highest merit scores. Rising Talent is the four whose score climbed most over "
            + "the last thirty days \u2014 a score that held or fell is not rising, and a member with no earlier day "
            + "to compare with is not yet. Category Leaders is the top score among the members who named each "
            + "kind of work, one card per kind that has somebody, strongest first. Delivery Champions is the four "
            + "best on-time records \u2014 dated milestones met on time as a share of those faced \u2014 with more "
            + "milestones first among equal shares.\n\n"
            + "Each panel shows one row of four; View all opens the leaderboard on the board closest to it. Every "
            + "card carries the same three figures \u2014 the star average from clients, opportunities won, and the "
            + "on-time share \u2014 and a dash where the portal has not read one yet.",
            Why: "A visitor deciding whether to hire here wants the answer to four different questions, and one "
                 + "ordering answers only one of them."),
        new("talent.filters",
            "Narrow every panel at once: kind of work, region, a merit floor, availability, years of experience, wins.",
            "Category and Region offer only kinds of work and regions with somebody on them. MeritScore keeps "
            + "members at or above a band\u2019s floor. Availability, Experience and Wins keep those who match: a "
            + "member who has not said when they can start, or how many years they have, is left out of a filter "
            + "that asks for it \u2014 the page lists what was said, not what was left unsaid. The filters live in "
            + "the address, so a narrowed page can be linked to.\n\n"
            + "The filters only narrow. What orders each panel is the portal\u2019s own record, and nothing a member "
            + "wrote about themselves moves them up it.",
            Why: "A client with a brief for a mobile app wants the mobile specialists who can start this month, "
                 + "not the whole network."),
        new("talent.verified",
            "The tick beside a name: the portal itself has verified this member\u2019s work.",
            "A member wears the tick once a client has passed a verdict on them here \u2014 an opportunity won, or a "
            + "rating given after a paid award. It never comes from anything the profile says about itself: the "
            + "past projects a member lists are shown as self-reported, and the portal cannot check them.",
            Why: "A tick that could be earned by writing a good profile would say nothing; one stamped by a client "
                 + "who paid says the one thing a visitor cannot check for themselves."),
        new("profile.projects",
            "Past work, shown as self-reported — the portal cannot check it and never pretends to.",
            "A link or a public repository anyone can open is worth double a paragraph nobody can, "
            + "both to the merit score and to the client reading it. Pictures are worth as much again: "
            + "four to a project, shrunk in your browser before they are sent, and the whole screenshot "
            + "rather than a crop of it.\n\n"
            + "Each project takes the kind of work it was from the same list an opportunity is filed under, "
            + "so a brief and a portfolio are described in one vocabulary, and a completion date as "
            + "month and year.\n\n"
            + "Outcome is what came of the work: the result or the benefit, such as time saved, sales won "
            + "or people using it. The description says what you built; the outcome says what it did, "
            + "and it is the line a client reads for that.\n\n"
            + "Nothing is shown to anybody else until you tick that you may show it publicly. Work under "
            + "an agreement that forbids showing it is still worth writing down — it is what you have "
            + "done — and unticked it stays on your own page, out of everybody else's and out of the "
            + "summary the portal drafts for you.\n\n"
            + "The verified half of your record is a different list on the same page: the opportunities you "
            + "entered here, the milestones you claimed, and what clients said afterwards."),

        new("entry.remove",
            "Takes one entrant off this opportunity, tells them why, and closes the door behind them.",
            "For an entry that does not belong here: a duplicate of another entrant's repository, an "
            + "empty or automated submission, abuse, or work that breaks the terms you published.\n\n"
            + "Your reason is emailed to them word for word and is the only account they will get, so "
            + "write the sentence you would be willing to defend. Their repository is archived at once — "
            + "read-only, so nothing more can be pushed — and they keep read access to it for a week so "
            + "they can clone what they built. It is never transferred to you and never deleted.\n\n"
            + "They drop off the entrant list and the board immediately, and they cannot enter this "
            + "opportunity again. Nothing about it appears on their public record.",
            Why: "It cannot be undone: there is no way to put an entrant back, and somebody who staked "
                 + "days of work is being told it does not count. If the problem is with the whole "
                 + "opportunity rather than one entry, cancel the opportunity instead — that tells everybody, "
                 + "with the same reason, and goes on your record rather than theirs."),

        new("opportunity.attachments",
            "Files entrants build against — logo masters, sample data, mockups. Locked at publish, like the brief.",
            "Attach what the words of the brief cannot carry: brand assets, spreadsheets of real (anonymised) "
            + "data, annotated screenshots. Files upload straight to the portal's storage and preview here as "
            + "they will on the opportunity page, where signed-in users see them once it is published: pictures, "
            + "PDFs, video, audio and text (notes, CSV, JSON, Markdown up to 1 MB) show in place, and anything "
            + "else is a download.\n\n"
            + "Attachments are part of the brief, so they share its one-way door: add and remove freely while "
            + "drafting, frozen the moment you publish — entrants commit their time to these files too.",
            Why: "An entrant who builds against the wrong logo master or stale sample data wastes their week and "
                 + "blames the opportunity. What you attach here is a term of the deal, so check it like one."),

        new("opportunity.publish",
            "Publishing lists the opportunity publicly and locks the brief, milestones, deadline, and award.",
            "A draft is yours to edit and invisible to everyone else. Publishing puts it in the feed, opens entry, "
            + "and freezes its terms — from that moment, freelancers are spending real time against them.\n\n"
            + "Read the brief once more as a stranger would before you press this.",
            Why: "This is the one-way door in the opportunity lifecycle. Changing terms under entrants who already "
                 + "committed is not supported — that protection is what makes entering worth anyone's while."),

        new("entry.githubUsername",
            "The GitHub account that will receive your private opportunity repository.",
            "The portal creates a private repository for your entry inside the opportunity organization and invites this "
            + "account to it. Your code stays private — the client only gets read access after the deadline, and "
            + "other entrants never see it.\n\n"
            + "Use the exact username, not your display name or email.",
            Why: "A typo here means the repository invitation goes to the wrong account — possibly a stranger's. "
                 + "Check it before entering; the portal cannot tell a typo from a valid account that is not yours.",
            Example: "bilal-builds"),

        new("opportunity.delivery",
            "How entrants hand work in: a private GitHub repository each, files uploaded to this page, or both.",
            "A repository suits code — commits, tags and pull requests are the record, and the winning "
            + "repository transfers to you when you pay. An upload suits work with no commits: a logo, a "
            + "document, a deck. Entrants hand files in on the opportunity page, the files freeze at the deadline "
            + "exactly as a repository does, and you review them here before announcing.\n\n"
            + "Both means a repository each and an upload panel — for a build that comes with design files "
            + "or a written report alongside the code. Either way milestones are claimed the same way: a "
            + "pushed tag, or a file tagged to the milestone.",
            Why: "Frozen at publish with the rest of the terms — an entrant with no GitHub account decides "
                 + "whether to enter on this, and one who entered for a repository cannot be moved to an "
                 + "upload halfway. Pick what the work is actually shaped like, not what is easiest to review."),

        new("opportunity.requiresCompose",
            "Entries must start with one command: a Docker Compose file at the root of every repository, built at each milestone.",
            "Tick this for work a client should be able to run — a web app, an API, a dashboard. Every milestone an "
            + "entrant claims is then built from their repository's compose file on the portal's build host, and the "
            + "board shows Builds or Build failed beside the claim, with the build log a click away. A failed build "
            + "still counts as the claim; it lowers the entrant's standing and tells them to fix it. Where the portal "
            + "runs previews, a built milestone can also be started and opened — you see the product as it stood, "
            + "before the deadline too, never the code — and after the deadline the final version of each entry.\n\n"
            + "Leave it off for work that is not a running program — a library, a command-line tool, a mobile app, a "
            + "design — or the compose file is a hoop with nothing behind it. Only with a repository delivery, and "
            + "the builds run only once an administrator has set up the build host under Settings.",
            Why: "Frozen at publish with the rest of the terms: entrants decide whether to enter on it, and a repository "
                 + "without a compose file shows Build failed on every claim. Ask for it only where the work genuinely runs."),

        new("preview.start",
            "Runs this milestone's commit on the build host, at an address only the entrant, the client and admins can open.",
            "Start preview checks out the claimed commit and starts it from the images its build left, which takes "
            + "seconds; Open preview then opens it in a new tab. Each press makes a link that works once and sets a "
            + "cookie for that preview alone — to open it on another device, sign in and press it there. Test logins and notes the "
            + "entrant put in a PREVIEW.md at the root of the repository show under the button.\n\n"
            + "A preview nobody has opened for a while stops itself (the idle stop, set by the administrator); Start "
            + "preview brings it back. Stop takes it down at once. Only a few run at a time on the build host, so stop "
            + "one you are done with.",
            Why: "The client sees the product before the deadline — never the code. A preview runs with no access to "
                 + "the portal: it lives on a domain of its own, and the portal's sign-in never reaches it."),

        new("preview.runFinal",
            "Runs the final tag the deadline froze on the build host: the client tries it, the entrant sees what they will see.",
            "Run final checks out the final tag, builds it and starts it, which can take a few minutes the first "
            + "time; Open preview then opens it in a new tab, and the entrant's PREVIEW.md notes (test logins, what "
            + "to try) show under the button. It stops itself after a while without a visit; Run final again brings "
            + "it back, from the images it built the first time. The client runs it from the review list, the "
            + "entrant from their own panel on the opportunity page; either one's preview is the same one.",
            Why: "Reading the code shows how it was built; running it shows whether it works. Both come before announcing: "
                 + "once announced, the winner is the winner."),

        new("entry.upload",
            "Hand your work in as files. They freeze at the deadline; the client sees them from then on.",
            "Upload what the brief asks for — up to 20 files, each under the portal's size limit. Tag a file "
            + "to a milestone to claim it: the board records the claim within seconds, first file wins, and "
            + "like a pushed tag it is never undone, so tag the version you stand by. Each file previews here "
            + "as the client will see it: pictures, PDFs, players and text in place.\n\n"
            + "Until the deadline you can add files and delete the ones that claimed nothing. At the deadline "
            + "everything freezes: what is here then is what is judged, and a file that lands afterwards is "
            + "not kept. The client cannot open your files before the deadline — only how many you have "
            + "uploaded shows on the board — and other entrants never see them at all.",
            Why: "A first draft uploaded early is not held against you: nobody but you opens it until everyone "
                 + "has handed in. But a milestone claim is a public statement that the work is done, and it "
                 + "cannot be taken back."),

        new("entry.files",
            "The files this entrant handed in, as they stood at the deadline. Yours to download and keep.",
            "Every file is shown as a brief's attachments are: pictures, PDFs, video, audio and text (notes, "
            + "CSV, JSON, Markdown up to 1 MB) preview in place, and anything else is a download. A file marked "
            + "with a milestone number is the one that claimed it on the board. Click an image to see it larger, "
            + "and download the real file before you judge it: a logo that reads at thumbnail size can fall apart "
            + "at 24px, and the brief's constraints are only checkable on the file itself.\n\n"
            + "Nothing here transfers on payment the way a repository does — the files are already yours to "
            + "download, from the moment the deadline passed.",
            Why: "This is the whole of what an upload opportunity delivers. If a constraint in the brief cannot be "
                 + "checked from these files, the winner you announce is a guess."),

        new("entry.withdraw",
            "Withdrawing takes you out of this opportunity for good — you cannot apply to it again.",
            "Your entry stops counting, leaves the entrant list and the board, and your repository is archived "
            + "rather than deleted; files you handed in stay yours. Your application reads Withdrawn, and that is "
            + "final: a freelancer applies to an opportunity once, whatever became of it. Every other opportunity is "
            + "unaffected. The box for why is optional. What you write is shown to the client under your "
            + "Withdrawn application, and to the portal's administrators.",
            Why: "Withdrawal is immediate and cannot be undone. If you are only stuck, keep the entry — an "
                 + "unfinished entry costs you nothing, but a withdrawal cannot win."),

        new("entry.claimMilestone",
            "Claim a milestone from inside your repo: push a tag m1, m2, … or open a pull request.",
            "When a milestone genuinely works, tag it — `git tag m2 && git push origin m2` — or open a pull "
            + "request from a branch named like `m2-invoicing`. The webhook stamps the claim within seconds and "
            + "the opportunity board shows it, with the commit it points at.\n\n"
            + "Claims are first-tag-wins and cannot be unclaimed, and the client sees the commit your tag points "
            + "at — so claim when it works, not when you hope it will.",
            Why: "The board is how the client judges who is actually progressing, and an entrant with no claims "
                 + "halfway through an opportunity looks abandoned. Claiming honestly is also your protection: a stamped "
                 + "checkpoint with a commit hash is evidence of what you had built, and when.",
            Example: "git tag m1 && git push origin m1"),

        new("github.connect",
            "Connecting proves you own the GitHub account the winning repository will transfer to.",
            "The button sends you to GitHub to authorise the portal's app, which hands back your account's "
            + "numeric ID — nothing is typed, so nothing can be mistyped. The portal stores that ID and uses it "
            + "twice: to grant you read access to every entry's repository when review opens, and to transfer "
            + "the winning repository to you once you mark the award paid.\n\n"
            + "The portal never gets your password and cannot act as you — the authorisation only identifies "
            + "your account.",
            Why: "A typed username would be wrong in two invisible ways: a typo only surfaces when the transfer "
                 + "fails — after you have paid — and GitHub logins can be renamed, silently breaking any stored "
                 + "string. The connected numeric ID never changes."),

        new("award.announce",
            "Announcing publishes the winner and closes the opportunity for everyone. It cannot be re-run.",
            "The award row freezes the winner and the amount as announced, every losing repository is archived, "
            + "and the opportunity shows its result publicly. Entrants' code stays theirs — nothing transfers yet.\n\n"
            + "Review every repository you intend to before this step; the entrants' work is frozen at the "
            + "deadline tag, so what you see is what was submitted.\n\n"
            + "An entrant's row may carry their past work that fits your brief and what came of it. It comes "
            + "from their profile as they wrote it, so it is their own account, not something the portal checked.",
            Why: "This is the second one-way door in the opportunity lifecycle. Losing entrants are told they lost "
                 + "— reversing a public result afterwards is a dispute, not a click. The award itself is only "
                 + "a promise until you mark it paid."),

        new("apply.how",
            "You apply to compete; the client selects who does. An entry is made only from a selected application.",
            "Applying takes eight steps: the opportunity in figures, how you match it, whether you are eligible, "
            + "your professional summary, how you would approach the work, the past work that proves it, the "
            + "time you commit, and a final review. Nothing is sent until you submit on the last step. The "
            + "client — or a portal administrator — reads every application and selects who competes; a "
            + "selected application becomes an entry, and a private repository is set up where the opportunity "
            + "uses one. You are emailed either way.",
            Why: "A proposal is something you hope somebody reads. Your profile, your past work and your record "
                 + "here are already on file — the application puts them in front of the client in order, and "
                 + "asks you for the one thing they are not: how you would do this particular job."),

        new("apply.match",
            "The same figure as the ring on the card: the four lines of the assessment, averaged, skills counting double.",
            "Expertise in the required skills, relevant past work, your on-time record here, and your "
            + "availability against the timeline — each a number out of 100 with the reason under it, and the "
            + "weakest one worded as a risk. All of it is arithmetic over your profile and what the portal has "
            + "watched happen; no model is asked. The recommendation card says what the portal would do with "
            + "the figure: apply, or strengthen the profile first."),

        new("apply.eligibility",
            "The door’s own checks: the merit floor, the required skills, a confirmed account, and your availability.",
            "The merit floor and the required skills are terms the client set and the portal refuses an "
            + "application short of either — add what is missing to your profile and the page checks again. "
            + "A confirmed email or phone is what every account has; a verified mobile is noted, not required. "
            + "Availability reads what your profile says: “not taking work on” is noted here so you can change "
            + "it before you commit to a timeline."),

        new("apply.summary",
            "Your profile’s About, as the client will read it on this application. Edit it here without touching the profile.",
            "This is what replaces a proposal: the paragraph that introduces you. It starts as your profile’s "
            + "About and is saved with the application as you leave it — your profile is unchanged. Where AI "
            + "drafting is on, the portal can tighten it from the rest of your profile; you approve, edit or keep "
            + "your own."),

        new("apply.approach",
            "How you would do this job: phases, architecture decisions, risks, delivery — 100 to 1000 characters.",
            "Competition is about execution, and this is the one part of an application the profile cannot "
            + "supply. Say what you would build first, what you would decide and why, where the risk is and how "
            + "you would deliver against the milestones. The client reads it beside your past work; specific "
            + "beats polished."),

        new("apply.aiApproach",
            "Drafts an approach from the brief and your profile. You edit and approve before submitting.",
            "The model reads the brief — its required skills, milestones and requirements — and your own skills "
            + "and the past work that fits, and drafts the phases, the decisions, the risks and the delivery "
            + "strategy in your voice. Where you have typed something, it tightens rather than replaces. "
            + "“Use suggestion” puts the draft in the box, “Edit” puts it there and hands you the cursor, "
            + "“Keep mine” leaves yours alone. Nothing is submitted for you.",
            Why: "A blank box is where most applications stall; a draft you must read and edit is a start, "
                 + "not a proposal written for you."),

        new("apply.portfolio",
            "The past work that supports this application, with how relevant the portal reads each project as.",
            "Every project on your profile, with a relevance figure: filed under the opportunity’s kind of work is "
            + "most of it, and how many of the required skills the project’s own words mention is the rest. "
            + "Projects at 60 or more are highlighted and ticked for you; tick or untick any. The ones you leave "
            + "ticked travel with the application as they stand today — a later profile edit does not change "
            + "what the client read."),

        new("apply.availability",
            "What you commit to for these weeks — separate from what your profile says in general.",
            "The four confirmations are the terms of competing and all four are required. Full, partial or "
            + "limited availability and the hours a week are this application’s own answer, saved with it; your "
            + "profile’s availability is not changed. The competitive advantages are a closed list the client "
            + "reads on every row — the portal ticks the ones your profile supports, and you choose."),

        new("apply.review",
            "What the client will read, in one panel. Submitting files the application for review.",
            "The figures are the ones on the earlier steps — the match, your merit score, the projects and the "
            + "hours you chose — and the checklist says which steps are complete. Submitting puts the "
            + "application under review; the client selects who competes and you are told either way."),

        new("apply.status",
            "Where your application stands: filed, evaluated, with the client, decided, and the start.",
            "Application submitted is the moment you pressed the button. AI evaluation is the portal wording "
            + "its own assessment, where that feature is on; the figures are there whether or not it has. Client "
            + "review is the client reading it; competitor selection is their decision; competition start is "
            + "the opportunity’s start day, once you are selected. “Stronger than” compares your match figure with "
            + "the other applicants’ at the time each applied. Once the client decides, the page says what comes "
            + "next: the competition’s start if you were selected — and, once it ends, how it went — and what to "
            + "strengthen if you were not. An opportunity that closes before the client decides says so, and nothing "
            + "more happens to that application; a selected entry you withdraw reads Withdrawn."),

        new("apply.evaluation",
            "The portal’s reading of your application: the match, and a strength or risk per line of the assessment.",
            "The figure and the lines are arithmetic — the same four lines as the match, plus whether past "
            + "work was attached. Where AI evaluation is on, the model puts each line into a phrase about your "
            + "application and adds a note for the client; it cannot add a line, drop one or say whether to "
            + "select you. The client reads the same box."),

        new("apply.advice",
            "Read off the portal’s evaluation of your application — the client is not asked for reasons.",
            "Strong is what held up: the kind of work, where the brief’s matched yours, or otherwise the "
            + "strengths the evaluation named. Improve is one line for each weakness it found — past work that "
            + "fits, the required skills on your profile, your delivery record, your hours for the timeline, the "
            + "kind of work — and competition experience until your delivery record reads strong. It is "
            + "arithmetic, not a guess at why the client chose others: they may simply have had fewer places "
            + "than strong applicants."),

        new("apply.mine",
            "Your applications: the ones waiting on a client, and the ones not selected.",
            "An application is listed here while the client is deciding, and afterwards if they did not "
            + "select you — with the date, so you know it was answered rather than lost. A selected one is not "
            + "listed twice: selection makes your entry, and the entry below says when. Anywhere on a row opens "
            + "the application as you filed it, and where it stands. An opportunity that closed before the client "
            + "decided says so; nothing more happens to that one, and it counts against nothing."),

        new("application.decide",
            "Select puts the applicant in the opportunity; Not selected tells them no. Either can change while it is open.",
            "Selecting makes an entry by the same rules the door has always had — the merit floor, the "
            + "required skills, the last joining date and the portal’s cap are checked again at that moment, "
            + "and a selection short of one is refused with the reason. A private repository is set up for the "
            + "entrant where the opportunity uses one, and they are emailed the terms. Not selected emails the "
            + "applicant that it is not held against them.\n\n"
            + "While the opportunity is open an answer can be changed. A turned-down applicant can still be "
            + "selected. A selection can be taken back until their work arrives — a push, a claimed milestone "
            + "or a file handed in: the entry steps aside, any repository made for it is archived, and they are "
            + "told. Once work has arrived, taking somebody out is Remove from opportunity, with a reason they "
            + "receive word for word and a week to clone their repository; the application then reads Removed, "
            + "and that is final. So is Withdrawn, when a selected entrant withdraws; the reason they gave, if "
            + "they gave one, shows under it. A freelancer applies to a "
            + "opportunity once.\n\n"
            + "Once the opportunity moves to review the box stays, to read: no answer can change any more, an "
            + "application nobody answered says so, a withdrawal still shows its reason, and a selected entrant "
            + "can still be removed.",
            Why: "Open entry put everybody who passed the door into the opportunity at once. Selection keeps the "
                 + "door and adds a reader — you see the approach, the past work and the hours before anybody "
                 + "stakes a week on your brief."),

        new("opportunity.cancel",
            "Cancelling calls the opportunity off for everyone, permanently, and goes on your public record.",
            "Every entry is voided, every entrant gets an email carrying your reason word for word, and their "
            + "repositories are archived — read-only, never deleted, never transferred, so their work stays "
            + "theirs. The opportunity page stays up showing the cancellation notice.\n\n"
            + "It also becomes part of your track record: an opportunity cancelled after entrants joined is counted "
            + "beside your payment history on every opportunity you post afterwards.",
            Why: "Entrants staked real work on this brief with only your promise behind it. The permanent, "
                 + "public mark is what keeps that promise from being free to break — so cancel for a real "
                 + "reason, not a change of mood, and say the reason plainly: it is the last thing the portal "
                 + "sends your entrants on your behalf."),

        new("award.markPaid",
            "Confirms the money moved and hands the winning repository over. The transfer is the IP handover.",
            "Mark this only after you have actually paid the winner — the portal takes your word for it, and "
            + "this is the record both sides point to later. The moment it is marked, the portal requests the "
            + "repository transfer to your connected GitHub account.\n\n"
            + "GitHub holds a transfer to a personal account until you accept it — watch for the email. The "
            + "portal keeps checking and shows 'verified' only once the repository is really yours.",
            Why: "Payment is the trigger for the handover precisely because the announcement is a promise and "
                 + "the payment is the deal. Marking an award paid that was not paid gives the code away with "
                 + "nothing in return; there is no undo that returns the repository."),

        new("rating.stars",
            "One to five stars for how the deal went, from the only person who can know — the other side of it.",
            "Ratings open the moment the award is marked paid, in both directions: the winner scores the "
            + "client, the client scores the winner. Score the whole experience — the brief's honesty, the "
            + "communication, and above all whether the money and the code moved as promised.\n\n"
            + "Your rating is public immediately and follows the other person's name across the portal. You "
            + "can revise it later; the portal shows the current score and when it last changed.",
            Why: "Only people who finished a real award can rate, which is what makes the number worth "
                 + "anything — every rating on this portal cost somebody a paid award to give. Score it "
                 + "honestly: the next person deciding whether to work with them is relying on you."),

        new("rating.comment",
            "An optional public sentence about working with them. Shown wherever the score is shown.",
            "The stars say how it went; the comment says why. One or two concrete sentences beat a paragraph: "
            + "'paid the same day', 'brief changed halfway through', 'code was exactly what the milestones "
            + "promised'.\n\n"
            + "It is public and attributed to you by name, on this opportunity's page and on the other person's "
            + "record. Keep it about the work and the deal, not the person.",
            Why: "Comments are what future entrants actually read — a bare 3-star is ambiguous, but '3 stars: "
                 + "paid in full, three weeks late' tells the next freelancer exactly what to expect."),

        new("entry.zipDownload",
            "The whole entry as one ZIP — the frozen final tag, reviewable without a GitHub account.",
            "The portal packages the repository exactly as it stood when the deadline froze it, stores the "
            + "archive, and hands you a download link that lives for five minutes; ask again any time for a "
            + "fresh one. The first request does the packaging, so it can take a moment.\n\n"
            + "It is a snapshot of the files, not the repository: no commit history, no diffs, no in-GitHub "
            + "review, and no handover — the winner's repository still transfers through GitHub when you mark "
            + "the award paid. If you can review on GitHub, that is the richer view.",
            Why: "This is the fallback that keeps a review honest without a GitHub account. The archive is cut "
                 + "from the frozen tag, so what you download is exactly what the deadline locked — nobody can "
                 + "slip in a change after the freeze."),

        // ---------------------------------------------------------------
        // AI drafting surfaces. One rule appears in every topic because it
        // is the rule: AI drafts, humans decide.
        // ---------------------------------------------------------------
        new("opportunity.aiMilestones",
            "Drafts a milestone checklist from the brief and the terms above it. You edit and approve — nothing is saved for you.",
            "The model reads the form as it stands above this list — saved or not: the title and brief, the "
            + "kind of work, the required skills, the technical requirements and how work is handed in — and "
            + "proposes 3–8 ordered milestones, each meant to be verifiable from inside a repository, or from "
            + "the files handed in where that is the shape of the deal. Where entries must run with Docker "
            + "Compose, the first step is the one that runs. \"Use these\" replaces the list in the editor, "
            + "where every row stays editable until you publish.\n\n"
            + "The draft arrives dated and weighted, which is arithmetic rather than the model's opinion: the "
            + "steps are spread evenly from the competition start to the deadline, the last one landing on the "
            + "deadline itself, and the hundred percent of the work is split evenly between them. Set the "
            + "deadline before you draft and the dates come with it; draft without one and only the shares are "
            + "filled in.\n\n"
            + "One call per press, and the button waits until something it read has changed before it will "
            + "read again. It takes a few seconds, and the button says so while it works.",
            Why: "The milestone list becomes every entrant's progress board — the thing the webhook system tracks "
                 + "against. A good starting list makes every downstream progress signal more accurate, but it is "
                 + "your edit, not the model's draft, that entrants commit to."),

        new("opportunity.aiCoach",
            "Flags the brief's dispute fuel — ambiguity, missing constraints, no definition of done, terms that contradict it.",
            "The coach reads the whole form as it stands — you do not have to save first — and lists the "
            + "things that make two reasonable people disagree later: unstated technology constraints, "
            + "deliverables that could mean several things, no acceptance criteria, and a brief that argues "
            + "with the terms set beside it — a requirement it never mentions, a milestone it does not ask "
            + "for. Each flag is advisory — dismiss what you disagree with.\n\n"
            + "An empty list is a real answer: the brief reads clean. One call per press, and the same "
            + "unedited form is never read twice. Worth pressing once more when the rest of the form is filled "
            + "in, since that is when the terms are there to read against.",
            Why: "Disputed outcomes almost always trace back to a brief that meant different things to the client "
                 + "and the entrant. Catching that before publication is far cheaper than arbitrating it after."),

        new("opportunity.aiSeo",
            "Drafts the search title and description for the public opportunity page. Both stay yours to edit.",
            "Search engines show these two lines to freelancers looking for work. The draft is written from the "
            + "title, brief and kind of work on the form, saved or not; apply it, edit it, or ignore it — what "
            + "you save with the draft is what the page carries.\n\n"
            + "Leave the fields empty and the page falls back to the opportunity title and a generated line.",
            Why: "Freelancers arrive by searching — the same discoverability the public pages exist to serve. "
                 + "A human keeps the final say on public wording."),

        new("opportunity.aiCategory",
            "Asks the model which kind of work this brief describes.",
            "Reads the title and brief as they stand on this form — no need to save first — and suggests a "
            + "category, with the phrase in your own words that decided it. It works while you are creating "
            + "the opportunity, before there is anything saved at all.\n\n"
            + "One call per press; the button goes quiet until you edit the title or the brief, so the same "
            + "brief is never read twice. The suggestion is never applied for you, and where the brief is thin "
            + "it says so rather than guessing confidently.",
            Why: "The category is how entrants find this opportunity when they browse, so it is worth a second’s "
                 + "thought — but it is your call, and a wrong suggestion costs nothing to ignore."),

        new("opportunity.aiRequirements",
            "Drafts the requirements table from the brief, the kind of work and the skills above it.",
            "Reads the title, brief, kind of work and required skills as they stand on this form — saved or not "
            + "— and offers the constraints the brief states or clearly implies, one row each: the language, the "
            + "frameworks, the data, how it is served. \"Add these rows\" puts the ones not already in your "
            + "table under it, where every row stays editable; a row you already have is marked and left "
            + "alone.\n\n"
            + "Nothing the brief does not support is invented, and a required skill is not turned into a row "
            + "unless the brief makes it one. One call per press; the button goes quiet until something it read "
            + "has changed.",
            Why: "A constraint that only lives in the prose of the brief is the one an entrant misses, and the "
                 + "one that ends in a dispute. The table is where entrants look; this fills it from what you "
                 + "already wrote."),

        new("opportunity.aiCriteria",
            "Drafts the scoring rubric from the brief, the requirements and the milestones above it.",
            "Reads the brief, the technical requirements, how work is handed in and the milestone checklist as "
            + "they stand on this form — saved or not — and offers three to six criteria with the points each "
            + "carries, adding up to a hundred and weighted by what the brief says matters most. \"Use this "
            + "rubric\" replaces the table, after a warning where you have typed one; every line stays yours "
            + "to edit until you publish.\n\n"
            + "The model judges nothing — it drafts the rubric you will judge by, from the work described and "
            + "never from who enters. One call per press; the button goes quiet until something it read has "
            + "changed.",
            Why: "The rubric is what you promise to choose by. Written from the brief before anyone enters, it "
                 + "is a promise; written around the entries afterwards, it is not."),

        new("opportunity.aiSpamScan",
            "Scans entries for empty repos, README-only work, and near-identical file layouts.",
            "Runs entirely on this server — no code or metadata goes to any provider, and no call quota is "
            + "used. The scan compares each entry's file listing against the others and flags what a human "
            + "should look at: repositories never pushed to, submissions that are only scaffolding, and pairs "
            + "of entries whose files overlap enough to suggest copying.\n\n"
            + "A flag never rejects anyone. It is a pointer for your review, nothing more.",
            Why: "Entry is open and uncapped by design, so at scale some entries will be noise. The scan keeps "
                 + "open entry workable without letting a machine decide who was serious."),

        new("entry.aiDigest",
            "A drafted reading aid for this entry — what was built and where to look first. Never a score.",
            "The digest summarises what the facts show: languages detected, milestone coverage, whether tests "
            + "and documentation exist, and 2–4 concrete places to start reading. It appears beside the code "
            + "as an aid, not instead of the code.\n\n"
            + "Unless the operator has enabled sending code excerpts to the model provider, the digest is "
            + "written purely from metadata the portal already holds — nothing from inside the repository "
            + "leaves this server.",
            Why: "Reading every entrant's repository is the slowest step in the whole opportunity. The digest speeds "
                 + "up your reading — it does not, and must not, rank entries or influence who wins. That "
                 + "decision is what entrants trusted you with."),

        new("opportunity.progressNarrative",
            "A daily AI-drafted note on board movement — who claimed what, who has gone quiet.",
            "Once a day the portal turns the board's raw claims and pushes into two to four plain sentences. "
            + "The note is drafted from exactly the data the board already shows and is labelled as AI-written.\n\n"
            + "Purely additive: with the feature off, the board still shows every commit, milestone, and "
            + "timestamp as before.",
            Why: "A board full of timestamps takes effort to read as a story. The note is the story — but the "
                 + "board below it stays the record."),

        new("opportunity.clientTrackRecord",
            "How this client's past promises went: awards paid, how fast, and what winners said.",
            "Everything in this box is derived from the portal's own records, never self-reported: opportunities "
            + "posted, awards announced and paid, the median days from announcement to confirmed payment, and "
            + "ratings left by past winners.\n\n"
            + "'Median days to pay' is the middle payment, so one slow dispute does not drown five same-day "
            + "payments. An award showing unpaid for weeks is the strongest signal here — the portal surfaces "
            + "the oldest one's age rather than hiding it in an average.",
            Why: "There is no deposit on this portal — entrants build first and are paid on the client's word. "
                 + "This record is the counterweight: a client who leaves awards unpaid carries that history "
                 + "into every opportunity they post. A brand-new client has no record yet, which is itself worth "
                 + "knowing before you commit a week of work."),

        new("register.role",
            "Clients post opportunities and pay awards; freelancers enter and build. Pick the side you are here for.",
            "A client account can post briefs, publish opportunities, and review entries. A freelancer account can enter "
            + "opportunities and gets a private repository per entry.\n\n"
            + "If you genuinely do both, register two accounts with different emails — the histories they accumulate "
            + "(awards paid, opportunities won) mean different things and are shown to different people.",
            Why: "The role is fixed at registration. Your public track record — payment history for clients, wins "
                 + "for freelancers — attaches to it from the first opportunity."),

        new("register.phone",
            "Optional. A code is texted here as well as emailed; either one confirms the account.",
            "Pick your country from the list — type a few letters of its name, or its dialling code, to "
            + "find it — and write the rest of the number the way you write it at home. The leading zero "
            + "most countries dial nationally is dropped for you. The full number the portal will text is "
            + "shown under the field; check it before you go on, particularly if you are not in the "
            + "country the list starts on.\n\n"
            + "It is never shown to other members; the portal keeps it only to text you a code. Leave it "
            + "blank and the code comes by email alone. On a portal that cannot send texts the field says "
            + "so, and the code comes by email whatever you enter here.",
            Why: "A number without a country code is a different phone in every country, and one country "
                 + "code wrong is a code texted to a stranger — which confirms nobody."),

        new("register.code",
            "The six digits from the email or the text. Either one works; the other is then unused.",
            "It is checked the moment the sixth digit is in; Confirm is there if that does not happen. "
            + "Codes work for fifteen minutes and you get five tries before the pair is thrown away — then "
            + "Send a new code, which is allowed once a minute. Nothing arrived? Check spam, then check "
            + "the address and number you typed; the screen shows both, partly hidden.",
            Why: "Until a code is entered the account can do nothing at all — not read an opportunity, not "
                 + "write a profile. That is deliberate: every member here is somebody who can be reached."),

        // The same field on a portal that cannot text: the form asks for
        // this one instead, so nobody is told to check a phone that was
        // never sent anything.
        new("register.emailCode",
            "The six digits from the email we sent you. It works for fifteen minutes.",
            "It is checked the moment the sixth digit is in; Confirm is there if that does not happen. "
            + "Codes work for fifteen minutes and you get five tries before the pair is thrown away — then "
            + "Send a new code, which is allowed once a minute. Nothing arrived? Check spam, then check the "
            + "address you typed; the screen shows it, partly hidden.",
            Why: "Until a code is entered the account can do nothing at all — not read an opportunity, not "
                 + "write a profile. That is deliberate: every member here is somebody who can be reached."),

        new("admin.newUser.type",
            "Freelancer, client, or administrator — chosen once, when you make the account, and never again.",
            "A freelancer enters opportunities and gets a private repository per entry. A client posts briefs, "
            + "reviews code and pays awards. An administrator can see and change every opportunity, account and "
            + "setting on this portal, including making and removing other administrators.\n\n"
            + "The person is told which kind you chose, in the invitation email, because it decides what the "
            + "portal will let them do the moment they arrive.",
            Why: "There is no changing it afterwards, and no screen that offers to. A person given the wrong "
                 + "type has to be deleted and made again, which is only clean before they have entered "
                 + "anything — an account with opportunities or entries against it can no longer be removed, only "
                 + "erased. Administrator especially: it is the one type that can undo every other."),

        new("admin.newUser.password",
            "Leave it blank to email an invitation; fill it in to set a password you then pass on yourself.",
            "Blank is the better of the two. The portal emails the person a link that sets their first "
            + "password, and nobody — you included — ever sees what they choose. The link lasts a week, and "
            + "you can send it again from this screen if it runs out.\n\n"
            + "Typing a password here is for the portal with no email configured, or the person who cannot "
            + "reach their inbox. It works immediately, but you know their password until they change it, "
            + "and so does anyone you pass it to.",
            Why: "An invitation cannot be sent at all unless SMTP is configured under Settings — without it, "
                 + "typing a password here is the only way to create a working account."),
        // ---------------------------------------------------------------
        // Dashboard metrics
        //
        // Every number on these screens is derived, and most of them use a
        // word that means something specific here — claimed, provisioned,
        // handover, outstanding. A metric someone could misread into a wrong
        // decision earns a topic; the self-evident counts do not.
        // ---------------------------------------------------------------
        new("dashboard.entrants",
            "Active entries across every opportunity you have published.",
            "Counted per entry, not per person, and only while the entry is active — withdrawing frees the "
            + "slot and drops the entrant out of this number.\n\n"
            + "Entrants are the applicants you selected, and this is the number they use to judge their odds "
            + "before spending a week building.",
            Why: "An opportunity attracting far fewer entrants than your others usually means the brief is unclear "
                 + "or the award is low for the work — both are fixable while the opportunity is still open."),

        new("dashboard.applicationsWaiting",
            "Applications under review on your open opportunities — each applicant is waiting to hear whether they compete.",
            "Counted only while an opportunity is open, because that is when an application can be decided; once a "
            + "opportunity moves to review, anything left undecided stays as it is and drops out of this number. "
            + "Select or Not selected in the opportunity page's Applications box. Needs you lists each opportunity with "
            + "applications waiting, the longest-waiting first.",
            Why: "An applicant keeps the weeks of your timeline free while they wait. A prompt no frees them for "
                 + "another opportunity; a prompt yes gives them the days to build."),

        new("dashboard.milestonesClaimed",
            "Checkpoints stamped by GitHub, not progress anyone typed in.",
            "An entrant claims a milestone from inside their repository — pushing a tag m1, m2… or opening a "
            + "pull request — and the webhook records which commit, and when.\n\n"
            + "The denominator is entrants × milestones: what the whole field could claim, not what one entrant "
            + "could. A board that is 30% full with ten entrants is normal.",
            Why: "Because it moves only when code moves, this is the earliest honest signal that an entrant has "
                 + "gone quiet — visible in week two rather than as a surprise at the deadline."),

        new("dashboard.awardOutstanding",
            "Awards you announced but have not marked paid. Nothing transfers until you do.",
            "The announcement is a promise; the payment is the deal. The winning repository stays with the "
            + "portal's organisation until you mark the award paid.",
            Why: "Your payment record is public on every opportunity card you post. Freelancers decide whether to "
                 + "enter your next opportunity partly on this number, because the award is not held in escrow."),

        new("dashboard.awardValue",
            "Total announced, split into paid and outstanding, grouped by each opportunity's own currency.",
            "Opportunities carry their own currency, so the totals are never summed across them — one figure mixing "
            + "rupees into dollars would be worse than no figure.",
            Why: "The outstanding half is money someone is owed and code that has not changed hands. It is the "
                 + "one number on this screen with another person waiting behind it."),

        new("dashboard.activity",
            "One point per day for the last 30 days — what actually happened, not a running total.",
            "Flat stretches are real: an opportunity with no entries and no pushes reads as a flat line, which is the "
            + "point of plotting it daily rather than cumulatively. The days are your own time zone's, and a point opens "
            + "its day in a new tab: the opportunities published, the entries made or the entries that claimed a milestone "
            + "that day.",
            Why: "Activity that stops days before a deadline is the signal worth acting on — a nudge then is "
                 + "cheaper than an empty board at the freeze."),

        new("dashboard.attention",
            "Everything blocking somebody: unpaid awards, applications and opportunities to decide, pending transfers, broken repos.",
            "Ordered by cost, not by age. An unpaid award sits above an undecided opportunity because a person is "
            + "waiting on money, not on a click.\n\n"
            + "An empty list is meaningful — it means nothing in your opportunities is waiting on you.",
            Why: "Every item here has a counterparty. Left alone they become disputes, and a dispute costs far "
                 + "more than the minute it takes to clear the item."),

        new("dashboard.opportunityProgress",
            "Milestones claimed against milestones possible — that is entrants × milestones, for each opportunity.",
            "Sorted by how many entrants an opportunity attracted, so the opportunities with the most at stake are first.",
            Why: "An opportunity with many entrants and almost no claims usually means the milestones are unclear or "
                 + "far too large — worth fixing before the next one rather than after this one fails."),

        new("dashboard.opportunityMix",
            "Where your opportunities sit in the lifecycle: open, in review, awarded, draft, cancelled.",
            "In review means the deadline has passed, every repository is frozen at its final tag, and your "
            + "connected GitHub account has read access. That state ends when you announce a winner.",
            Why: "Opportunities parked in review are the usual source of complaints — entrants have finished and are "
                 + "waiting on a decision they cannot influence."),

        new("dashboard.deadlines",
            "Open opportunities closing soonest. At the deadline every repository freezes and review access is granted.",
            "The freeze tags the final commit, revokes each entrant's push access, and grants your connected "
            + "GitHub account read access to every entry — all automatically.",
            Why: "Connect your GitHub account before the deadline. Without it the freeze cannot grant you access, "
                 + "and the announce button stays disabled."),

        new("dashboard.recent",
            "The last milestones claimed, stamped by webhook the moment a tag or pull request lands.",
            "Each line names the entrant, the milestone, and how it was claimed. Deliveries are idempotent, so a "
            + "redelivery from GitHub never double-counts."),

        new("dashboard.winRate",
            "Opportunities you won, as a share of the opportunities you entered that have since been decided.",
            "Opportunities still open or in review are excluded — they have not been decided, so counting them would "
            + "drag the rate down for work that is still in the running.\n\n"
            + "It reads '—' until at least one opportunity you entered has been awarded.",
            Why: "A low rate across many entries usually means aiming too broadly. Entering fewer opportunities and "
                 + "finishing more milestones is the cheaper correction."),

        new("dashboard.earned",
            "Awards actually paid to you, and awards won but still awaiting the client's payment.",
            "The portal does not hold the award — the client pays off-platform and confirms it here, which is "
            + "what moves an award from awaiting to paid and starts the repository transfer to them.",
            Why: "If a paid award has not landed, the paper trail is here: announced, paid, and whether the "
                 + "repository handover was verified."),

        new("dashboard.entryProgress",
            "Your open entries, how far each is against its board, and when you last pushed.",
            "Ordered by deadline, soonest first. 'Repo pending' means the private repository is still being "
            + "provisioned — the invitation goes to the GitHub username you entered with.",
            Why: "The board is what the client watches. An entry with commits but no claimed milestones looks "
                 + "identical to an abandoned one from their side — push the tag."),

        new("dashboard.outcomes",
            "Every entry you have made, split by what became of it.",
            "'Not selected' counts entries in opportunities that were awarded to somebody else. Losing repositories "
            + "are archived, never transferred — your work stays yours and stays private."),

        new("dashboard.applicationsWaitingPortal",
            "Applications under review on every open opportunity — the clients’ decisions, and yours if one stalls.",
            "Counted only while an opportunity is open, because that is when an application can be decided. The "
            + "client decides in the opportunity page’s Applications box; an administrator can decide there too, "
            + "and the client is emailed when one does.\n\n"
            + "The line under the number says how long the longest-waiting applicant has waited, and the tile "
            + "turns amber once that is a week.",
            Why: "An applicant keeps an opportunity’s weeks free while they wait. A week without an answer usually "
                 + "means a client who has stopped reading, which is worth a message before it becomes an "
                 + "empty opportunity."),

        new("dashboard.repoHealth",
            "One private repository per active entry. Failed means an entrant cannot start work at all.",
            "Provisioning is queued and retried with backoff, so pending is normal for a short while after entry "
            + "and during a burst. Failed means the worker gave up and recorded why.\n\n"
            + "The usual causes are the GitHub App losing access to the organisation, a revoked installation, or "
            + "an entrant's username that no longer exists.",
            Why: "A failed repository is an entrant who cannot begin while the clock runs. It does not resolve on "
                 + "its own once the worker has given up."),

        new("dashboard.handoverStuck",
            "Awards paid whose repository transfer has not been confirmed by re-reading the owner.",
            "GitHub's transfer call returns 202 — accepted, not done. A transfer to a personal account waits until "
            + "the recipient accepts it, so the portal re-reads the repository and only then records it verified. "
            + "Once the repository is in the client's own account the App can no longer read it, so leaving the "
            + "organization is what counts — the award's note says the new owner was not seen directly.\n\n"
            + "GitHub lets an unanswered transfer invitation lapse after a day; the note says so when one has. "
            + "A transfer to an organisation the client can create in completes without that wait.",
            Why: "This is the one step where money has already moved. A transfer that never lands needs chasing "
                 + "while the paper trail is fresh — it is a dispute otherwise."),

        new("dashboard.webhooks",
            "GitHub deliveries in the last 24 hours, and how many matched no entry in this portal.",
            "Milestone claims, push counts and activity stamps all arrive this way. A handful of unmatched "
            + "deliveries is ordinary — a withdrawn entry, or a repository outside the portal.\n\n"
            + "In development GitHub cannot reach localhost at all; the compose file runs a smee tunnel that "
            + "forwards deliveries to the API.",
            Why: "A count that stays at zero while opportunities are active means claims are not being recorded — the "
                 + "board silently stops reflecting real work. Check the webhook secret and the App's events."),

        new("dashboard.signups",
            "New client and freelancer accounts per day over the last 30 days.",
            "Both sides plotted separately, because a marketplace short of one of them is a different problem "
            + "from one short of both. A deleted account is left out, here and on the People tile, as it is from "
            + "Users. A point opens, in a new tab, the clients or freelancers who joined that day."),

        new("dashboard.topClients",
            "Clients by opportunities posted, with what they attracted and whether they actually paid.",
            "'Awards paid' shows paid against announced. A client with announced awards they have not paid is "
            + "the pattern worth watching — the platform holds no deposit.",
            Why: "Payment history is what stands in for escrow here. Freelancers spend a week building on the "
                 + "strength of it, so a client who announces and does not pay costs the portal its entrants."),

        new("dashboard.topFreelancers",
            "Freelancers by active entries, with milestones claimed, pushes, wins and earnings.",
            "Entries counted while active; claims and pushes come from webhook deliveries, so they reflect real "
            + "repository activity rather than anything self-reported.",
            Why: "Many entries with no claims and no pushes is the shape of low-effort mass entry — the case the "
                 + "spam and duplicate filter exists for."),

        // ---------------------------------------------------------------
        // Admin console — actions on other people's stuck machinery
        // ---------------------------------------------------------------
        new("admin.retryProvision",
            "Puts a failed repository back at the front of the worker's queue with a clean retry budget.",
            "Retry does not fix anything by itself — it re-runs exactly what failed. Fix the cause first: the "
            + "usual ones are an entrant's mistyped GitHub username, the App losing access to the organisation, "
            + "or a revoked installation. The previous error stays visible until the outcome replaces it.\n\n"
            + "Only failed repositories in open opportunities can be retried; a pending one is still being retried "
            + "automatically.",
            Why: "On success the entrant is emailed that their repository is ready — so a retry that keeps "
                 + "failing silently is an entrant who was promised nothing and told nothing. Check the note "
                 + "after retrying."),

        new("admin.restartHandover",
            "Re-runs the repository transfer for a paid award — with the client's current GitHub login.",
            "The payment path checks three things: GitHub configured, the winning entry has a repository, the "
            + "client has a connected account. This action re-checks them now instead of copying the answer "
            + "from payment time — the point is that the world has changed since: GitHub was configured later, "
            + "or the client finally connected. It also refuses while the GitHub setup has no usable transfer "
            + "token, because the transfer could only fail again.\n\n"
            + "GitHub holds a transfer to a personal account until the recipient accepts it, so \"requested\" "
            + "is normal for a while; the portal re-reads the repository and records \"verified\" only when it "
            + "sees the new owner.",
            Why: "Every award here has money already moved. Restarting is safe — the transfer call is the same "
                 + "one payment fires, and the client is emailed to accept it — but a handover that stays "
                 + "unverified after a restart needs a human on GitHub's side, not another restart."),

        new("admin.replayDelivery",
            "Runs a stored webhook delivery through the live handler again. Claims recover; nothing double-counts.",
            "The classic use: a delivery that said \"no matching entry\" because it arrived before the entry's "
            + "repository was recorded — replay it after the fix and the milestone claim lands. Claims are "
            + "idempotent (first claim wins, enforced by the database), so replaying a delivery that already "
            + "worked is a no-op.\n\n"
            + "Push-activity stamps are deliberately not replayed: restamping \"last push\" with today's date "
            + "would make an abandoned entry look freshly active.",
            Why: "The note keeps both halves of the story — what live handling did, then what the replay did. "
                 + "A replay that still says \"no matching entry\" means the underlying fix has not landed."),

        new("admin.handovers",
            "Every award's paper trail: announced, paid, transfer requested, verified — four stamps, in order.",
            "This is the record a dispute is judged by. Announced without paid is a client who owes money; paid "
            + "without a transfer requested is code the portal still owns; requested without verified is a "
            + "transfer waiting on the recipient — three different problems wearing similar badges.",
            Why: "When a winner says \"I was never paid\" or a client says \"I never got the code\", the answer "
                 + "is one of these rows. The stamps are written by the machinery, not typed by anyone."),

        // ---------------------------------------------------------------
        // Users — an administrator's hand on somebody else's account
        // ---------------------------------------------------------------
        new("admin.resetVerification",
            "The verdict and its proof: what the provider read off the document, its images, its whole decision.",
            "Once a verdict lands the portal reads the provider's whole decision and copies every image it "
            + "links to — the front and back of the document, the portrait read off it, the selfie — into the "
            + "portal's own file storage, because the provider's links expire. The panel shows the facts an "
            + "administrator reads first (name, document, number, dates, how the checks scored), the images "
            + "(click one to open it full size through a five-minute link; every opening is a row in Activity), "
            + "and the full decision underneath. While the copy runs the panel says so; if it falls short — "
            + "storage not configured, the provider refusing — it says why, and Fetch again tries once more.\n\n"
            + "Take verification back is for a verification that should not stand — a document that turned out "
            + "to be somebody else's, or a member who asks to start over under a corrected name. The provider's "
            + "record is not touched; the portal forgets the verdict, its session and its proof (the stored "
            + "images are deleted), the verified mark leaves the profile, and the next start opens a fresh "
            + "session. Nothing the person owns changes: opportunities stay open, entries stay in, and a client who "
            + "already copied their payment details keeps what they copied.",
            Why: "These are copies of a government identity document and a face. Only administrators see them, "
                 + "they go when the account is erased or the verification is taken back, and your privacy "
                 + "policy must say the portal keeps them. Taking a verdict back is reversible only in the sense "
                 + "that the person can verify again; there is deliberately no button to put it back by hand."),

        new("admin.lockUser",
            "Keeps the person out from their next request — every session ends at once — and touches nothing they own.",
            "A lock is reversible and leaves no mark: opportunities stay open, entries stay in, repositories stay "
            + "theirs. The login door answers \"this account is locked\" and nothing more; the reason you write "
            + "here is a note to yourself and is never shown to the person.\n\n"
            + "You cannot lock your own account, or the last administrator who can still sign in.",
            Why: "Locking is the safe first move when something looks wrong — a disputed entry, a password that "
                 + "may have leaked — because unlocking undoes all of it. Deleting does not."),

        new("admin.resetPassword",
            "Two ways back in: email them a one-hour reset link, or set a password yourself and pass it on.",
            "The link is the one the forgot-password form sends, and works once. Setting a password directly is "
            + "for a portal without email, or a person who cannot reach their inbox — hand it over out of band "
            + "and ask them to change it.\n\n"
            + "Either way, every session the account had is signed out the moment the new password takes.",
            Why: "A password you set is a password you know. Prefer the link whenever email works, so nobody but "
                 + "the account holder ever sees the secret."),

        new("admin.deleteUser",
            "Removes the person and keeps the record. An account nothing points at goes entirely.",
            "Opportunities, entries, awards and ratings are other members' records too: a paid award with no winner on "
            + "it is a dispute nobody can settle. So an account they point at is erased in place, shown as "
            + "“Deleted member” wherever those records appear — while an account with no history is removed "
            + "outright.\n\n"
            + "Erasing takes the person out of the account: name, email, phone, password, GitHub link and "
            + "devices are wiped, and their payment details deleted outright. The profile they wrote — "
            + "headline, introduction, location, skills, languages and portfolio — stops being readable "
            + "anywhere on the portal, but the row is kept, marked with the date and the administrator who "
            + "deleted the account, so a deletion can be answered for afterwards. An account removed outright "
            + "keeps nothing at all. What stays either way is the record itself: which opportunities they ran or "
            + "entered, what they won, and how they were rated.\n\n"
            + "Whatever the person left running is closed on the way out: their open opportunities are cancelled and "
            + "every entrant told why, and their active entries are withdrawn.",
            Why: "There is no undo. A lock keeps everything and can be lifted; reach for delete only when the "
                 + "account itself has to go."),

        // ---------------------------------------------------------------
        // Activity — what everybody did, for an administrator to read back
        // ---------------------------------------------------------------
        new("admin.database.test",
            "Proves a database before anything is staked on it: reachable, new enough, full-text where it matters, and empty.",
            "The connection string is the whole address: for SQL Server, Server (host, or host,port), Database, "
            + "User Id and Password — or Integrated Security=True for the service's own account — and "
            + "TrustServerCertificate=True where the server's certificate is self-signed, which a container's is; "
            + "for PostgreSQL, Host, Port, Database, Username and Password. SQL Server must be 2022 or later with "
            + "Full-Text Search installed; the test says which is missing.\n\n"
            + "Empty means no tables, or only the portal's own with no account, setting or opportunity in them — what a "
            + "move that failed leaves, and may be tried against again. A SQL Server database that does not exist "
            + "yet passes too: the move creates it. A PostgreSQL one has to exist first, because creating it is "
            + "the operator's choice of owner and encoding.",
            Why: "A move copies into whatever the string names. Testing first is how a typo does not become a move "
                 + "onto the wrong server."),

        new("admin.database.move",
            "Copies every table to another database, records the choice beside the keys, and restarts the API on it.",
            "In order: the target is tested; the portal pauses — every write is refused with a 503 until the API is "
            + "back, and the background workers sit out their cycles; the tables are created on the target; every "
            + "row is copied in one transaction, parents before children; the row counts are compared; the choice "
            + "is written to database.json beside the data-protection keys; and the API stops on purpose so the "
            + "service manager brings it back on the new database — compose under restart: unless-stopped, the "
            + "Windows service under its restart-on-failure rule. About half a minute of downtime for a portal "
            + "this size, most of it the restart.\n\n"
            + "A failure rolls the target back, lifts the pause, and leaves the reason on this page; the portal "
            + "stays where it was. The old database is never touched by a move — after a successful one it is "
            + "yours to back up and drop. To return to the environment's database, delete database.json from "
            + "the keys directory and restart.",
            Why: "This is the one action here that ends the API process. Do it at a quiet hour, with a fresh backup "
                 + "of the old database, and not from a laptop that may sleep before the page says the API is back."),

        new("setup.database",
            "Keep the database the portal started on, or move it to the other one before anybody uses it.",
            "The portal is already running on the database its environment named — SQL Server from the compose "
            + "stack, or whatever the service was installed against. Keeping it is the ordinary choice. Choosing "
            + "the other database here runs the same move an administrator can run later from Admin → Database, "
            + "on a portal that holds only your account and these settings: the target is tested, the tables "
            + "are created, the little there is copied, the choice recorded beside the keys, and the API "
            + "restarts on it — this page waits and follows.",
            Why: "Moving now costs nothing but the restart. Moving later costs the same, plus the minute the portal "
                 + "is paused with members in it."),

        new("admin.activity",
            "Every page opened, every action taken, and every call the portal made to another service, newest first.",
            "Three kinds of row. A page is the browser reporting where it went — signed in or not, so a "
            + "visitor's path through the public pages is here too, threaded by a session id that dies with "
            + "their browser. An action is the API recording a request that did something: a sign-in, an "
            + "application, an opportunity published, a setting changed, a file downloaded. A failed one is marked "
            + "with the answer it got. A third-party call is the portal itself calling another company's "
            + "API — the AI model, the identity provider, GitHub, the SMS gateway, the captcha check, a push "
            + "service, file storage — with how long it took and what it answered; choose Third-party to see "
            + "only those, and a service to narrow them further. An AI call names the provider and model it "
            + "really used, such as openai/gpt-5.5, under the vendor it called — members only ever see the "
            + "portal's own AI name — and with AI chosen, two more lists narrow to one provider or one model; "
            + "pressing a call's model does the same. Calls recorded before rows carried their model are left "
            + "out of those two. Open one to read the request and the "
            + "response. Keys, tokens, passwords and texted codes are replaced by [redacted] before the row "
            + "is written, a file's contents are never kept, and the identity provider's answer keeps its "
            + "statuses and ids but not the name, number or photographs it read off the document. A call "
            + "made by a background job, with nobody's click behind it, reads as the portal.\n\n"
            + "What a page or action row never holds: anything typed — except the reason given when a freelancer withdraws "
            + "or a client removes an entrant, shown under that action. It says a password was changed, never what to; a "
            + "opportunity edited, never the brief; a reset link opened, never its token. Reads are not rows "
            + "either — opening a page is one visit, however many calls the page made.\n\n"
            + "Choose an account to see only theirs; click a session id to follow one browser. Rows older "
            + "than the retention setting are swept once an hour.",
            Why: "This is how \"what did this account do\" and \"who changed that setting\" get answered "
                 + "after the fact — and it is personal data: addresses and browsing. Read it to answer a "
                 + "question, keep it as short as your questions need, and say in the privacy policy that "
                 + "it is kept."),

        new("admin.slowQueries",
            "Database commands at or over the slow-query threshold, one row per query in the code, the costliest in all first.",
            "A row is one SQL text: the same query run a thousand times is one row with a count of a thousand. Total is "
            + "what it has cost altogether — a query that is a little slow and runs on every page view outranks one "
            + "that is very slow once a day, and should. Slowest, average and the last run say whether it is always "
            + "slow or only sometimes; rows says whether it is slow because it returns too much. The SQL carries "
            + "placeholders where the values went, never the values.\n\n"
            + "The threshold is under Settings → Limits & maintenance (500 ms by default; 0 turns this off). "
            + "The tally lives in the API's memory: a restart starts it again, so does a new threshold, and so does "
            + "Clear — use it after an index or a fix ships, so what follows is measured against the change. Every "
            + "row is also in the API's log as a warning, which a restart does not lose.",
            Why: "This is the evidence a query should be rewritten, indexed or moved to hand-written SQL. A query that "
                 + "never appears here is not worth the work, however it looks."),

        // ---------------------------------------------------------------
        // Reports — what is yours as tables, to sort, group and take away
        // ---------------------------------------------------------------
        new("reports.opportunities",
            "Every opportunity that is yours to see, as one table to sort, group, total and export.",
            "Yours depends on who you are. An administrator sees every opportunity the portal has held, drafts and "
            + "cancellations included; a client sees the opportunities they posted, drafts included; a freelancer "
            + "sees the opportunities they applied to or entered, whatever came of it.\n\n"
            + "One row per opportunity: its client, status, kind of work, award, merit floor, how it is handed in, "
            + "the dates (created, published, start, deadline, entry close), its duration in days from start "
            + "to deadline, how many applied, how many entrants are competing, the milestone count, and — once "
            + "awarded — the winner. A client and an administrator also see how many applications are still "
            + "waiting or were selected, how many entrants left or were removed, and when the award was paid; "
            + "a freelancer sees instead where they stand in each, under Your part. Only an administrator's "
            + "report carries the client's email.\n\n"
            + "The filters panel folds away and narrows the table by words, status, client, category, delivery, "
            + "dates and award; every column header sorts (hold Ctrl to sort by more than one), and the column "
            + "menus filter one column at a time. Group by a column to see each group with its own count and "
            + "totals; the footer totals whatever is left after the filters. Columns can be hidden, resized and "
            + "dragged into a new order.\n\n"
            + "Print, CSV, Excel and PDF take the rows and columns as you have them — filtered, sorted and "
            + "grouped — not the whole table, so narrow first and export what you meant.",
            Why: "The feed shows what is open and each list shows a slice; this is the whole of what is yours "
                 + "— what was posted, for how much, and what came of it. Reading only — nothing on it "
                 + "changes an opportunity."),
        new("reports.applications",
            "Every application that is yours to see, as one table to sort, group, total and export.",
            "Yours depends on who you are. An administrator sees every application anybody has sent; a client "
            + "sees the applications to the opportunities they posted; a freelancer sees the applications they sent, "
            + "whatever came of them.\n\n"
            + "One row per application: the opportunity, the applicant, the client, the status (under review, "
            + "selected, not selected, withdrawn, removed) and the result — for a selection, how the opportunity "
            + "went (competing, work in review, won, not won, cancelled), and never answered for one still under "
            + "review when its opportunity closed. Then how it read when it was sent: the match and merit score, the "
            + "evaluation's word, the commitment, hours a week, the portfolio attached and the advantages "
            + "claimed. Then the answer: when it was sent, when the client first answered and how many days that "
            + "took, and for a selection the entry that came of it — milestones claimed, pushes, the last push, "
            + "the reason given for a withdrawal or a removal, and the day an award was paid. Only an "
            + "administrator's report carries the applicant's email.\n\n"
            + "The filters panel folds away and narrows the table by words, status, result, opportunity, applicant, "
            + "client, category, commitment, the applied and answered dates and the match; every column header "
            + "sorts (hold Ctrl to sort by more than one), and the column menus filter one column at a time. "
            + "Group by a column to see each group with its own count and averages; the footer counts and "
            + "averages whatever is left after the filters. Columns can be hidden, resized and dragged into a "
            + "new order.\n\n"
            + "Print, CSV, Excel and PDF take the rows and columns as you have them — filtered, sorted and "
            + "grouped — not the whole table, so narrow first and export what you meant.",
            Why: "An opportunity page shows its own applications; this is all of them side by side — who applied "
                 + "where, how they read, how long answers took, and what came of each. Reading only — nothing "
                 + "on it answers an application."),
        new("reports.entries",
            "Every entry that is yours to see, as one table to sort, group, total and export.",
            "Yours depends on who you are. An administrator sees every entry into every opportunity; a client sees "
            + "the entries into the opportunities they posted, a selection they took back included; a freelancer sees "
            + "the entries they made, whatever came of them.\n\n"
            + "One row per entry: the opportunity, the entrant, the client, the status (active, withdrew, selection "
            + "taken back, removed) and the result for an entry still in when its opportunity moved on (competing, "
            + "work in review, won, not won, cancelled). Then the board: the milestones claimed, how many on "
            + "time, late or overdue — read as it stood when the entrant left, or at the deadline, so dates that "
            + "came due afterwards are not held against anybody — the first claim and the days it took, the "
            + "pushes and the last push, the files handed in, and what the repository's listing shows. While the "
            + "opportunity is open or in review, an active entry's standing: its score out of 100, its place and its "
            + "reading. Then how it began and ended: the day entered, the application it came from and its match, "
            + "the day it ended and the days in, the reason given for a withdrawal or a removal, and for the "
            + "winner the days the award was announced and paid and the stars each side gave. Only an "
            + "administrator's report carries the entrant's email, and a freelancer's has no repository "
            + "handover.\n\n"
            + "The filters panel folds away and narrows the table by words, status, result, opportunity, entrant, "
            + "client, category, delivery, reading, the entered dates and the standing; every column header "
            + "sorts (hold Ctrl to sort by more than one), and the column menus filter one column at a time. "
            + "Group by a column to see each group with its own count, sums and averages; the footer does the "
            + "same for whatever is left after the filters. Columns can be hidden, resized and dragged into a "
            + "new order.\n\n"
            + "Print, CSV, Excel and PDF take the rows and columns as you have them — filtered, sorted and "
            + "grouped — not the whole table, so narrow first and export what you meant.",
            Why: "An opportunity's board shows its own entrants as they stand today; this is every entry side by side "
                 + "— who kept to the dates, who went quiet, who left and why, and who won. Reading only — "
                 + "nothing on it removes an entrant or picks a winner."),
    ];

    /// <summary>
    /// Form fields (not settings) whose help is load-bearing. The coverage
    /// test fails the build if one of these loses its topic.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredFormTopicIds =
    [
        "opportunity.awardAmount",
        "opportunity.startsAt",
        "opportunity.deadline",
        "opportunity.entryClose",
        "opportunity.brief",
        "opportunity.category",
        "opportunity.requiredSkills",
        "opportunity.minMerit",
        "opportunity.match",
        "opportunity.milestones",
        "opportunity.milestoneDates",
        "opportunity.milestoneShares",
        "opportunity.requirements",
        "opportunity.rubric",
        "opportunity.standing",
        "opportunity.publish",
        "entry.githubUsername",
        "entry.withdraw",
        "entry.remove",
        "profile.merit",
        "profile.about",
        "profile.photo",
        "profile.aiSummary",
        "profile.categories",
        "profile.skills",
        "profile.workType",
        "profile.whereYouWork",
        "profile.availability",
        "profile.languages",
        "profile.payment",
        "profile.strength",
        "profile.review",
        "profile.welcome",
        "profile.rank",
        "leaderboard.tabs",
        "leaderboard.rankBy",
        "leaderboard.delivery",
        "leaderboard.trend",
        "talent.panels",
        "talent.filters",
        "talent.verified",
        "profile.projects",
        "entry.claimMilestone",
        "github.connect",
        "award.announce",
        "award.markPaid",
        "opportunity.cancel",
        "register.role",
        "register.phone",
        "register.code",
        "register.emailCode",
        "admin.newUser.type",
        "admin.newUser.password",
        "rating.stars",
        "rating.comment",
        "opportunity.clientTrackRecord",
        "opportunity.attachments",
        "entry.zipDownload",
        "opportunity.delivery",
        "opportunity.requiresCompose",
        "preview.start",
        "preview.runFinal",
        "entry.upload",
        "entry.files",
        "opportunity.aiMilestones",
        "opportunity.aiCoach",
        "opportunity.aiSeo",
        "opportunity.aiCategory",
        "opportunity.aiRequirements",
        "opportunity.aiCriteria",
        "opportunity.aiSpamScan",
        "entry.aiDigest",
        "opportunity.progressNarrative",
        "opportunity.standingScore",
        "opportunity.aiStandingNotes",
    ];

    /// <summary>
    /// Dashboard metrics whose wording carries domain meaning — claimed,
    /// provisioned, handover, outstanding. Held to the same build-time rule
    /// as the forms: lose the topic, fail the test.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredDashboardTopicIds =
    [
        "dashboard.entrants",
        "dashboard.milestonesClaimed",
        "dashboard.awardOutstanding",
        "dashboard.awardValue",
        "dashboard.activity",
        "dashboard.attention",
        "dashboard.opportunityProgress",
        "dashboard.opportunityMix",
        "dashboard.deadlines",
        "dashboard.recent",
        "dashboard.winRate",
        "dashboard.earned",
        "dashboard.entryProgress",
        "dashboard.outcomes",
        "dashboard.repoHealth",
        "dashboard.handoverStuck",
        "dashboard.webhooks",
        "dashboard.signups",
        "dashboard.topClients",
        "dashboard.topFreelancers",
    ];

    /// <summary>
    /// Admin console actions. Every one of them restarts machinery that acts
    /// on somebody else's opportunity, so the explanation of what it re-runs —
    /// and what it deliberately does not — is part of the feature.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredAdminTopicIds =
    [
        "admin.retryProvision",
        "admin.restartHandover",
        "admin.replayDelivery",
        "admin.handovers",
        "admin.lockUser",
        "admin.resetVerification",
        "admin.resetPassword",
        "admin.database.test",
        "admin.database.move",
        "admin.deleteUser",
        "admin.activity",
        "admin.slowQueries",
    ];

    /// <summary>
    /// Reports every role reads. A report's help says whose rows it
    /// covers, which is the first thing anybody asks of a table.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredReportTopicIds =
    [
        "reports.opportunities",
        "reports.applications",
        "reports.entries",
    ];

    private static readonly Dictionary<string, HelpTopic> ById =
        All.ToDictionary(t => t.Id, StringComparer.Ordinal);

    public static HelpTopic? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Convention linking a setting key to its help topic.</summary>
    public static string TopicIdForSetting(string settingKey) => "settings." + settingKey;
}
