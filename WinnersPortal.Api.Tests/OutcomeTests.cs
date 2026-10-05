using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WinnersPortal.Api.Auth;
using WinnersPortal.Api.Common;
using WinnersPortal.Api.Middleware;
using WinnersPortal.Api.Settings;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Storage;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The seam between a service and the wire: every kind of outcome answers
/// with the status the endpoints answered with before the services existed,
/// and what an outcome says about the session becomes the cookie only where
/// it should.
/// </summary>
public class OutcomeTests
{
    /// <summary>Any answer at all, for the tests that are about something else.</summary>
    private sealed record Answer(bool Ok);

    private static HttpContext Context()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
    }

    private static async Task<HttpContext> AnswerAsync(IResult result)
    {
        var ctx = Context();
        await result.ExecuteAsync(ctx);
        return ctx;
    }

    private static string BodyOf(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    public static TheoryData<string, int> Kinds => new()
    {
        { nameof(OutcomeKind.Ok), 200 },
        { nameof(OutcomeKind.NoContent), 204 },
        { nameof(OutcomeKind.Invalid), 400 },
        { nameof(OutcomeKind.Unauthorized), 401 },
        { nameof(OutcomeKind.Forbidden), 403 },
        { nameof(OutcomeKind.NotFound), 404 },
        { nameof(OutcomeKind.Conflict), 409 },
        { nameof(OutcomeKind.TooManyRequests), 429 },
        { nameof(OutcomeKind.Unavailable), 503 },
        { nameof(OutcomeKind.Redirect), 302 },
        { nameof(OutcomeKind.File), 200 },
    };

    private static Outcome Of(string kind) => kind switch
    {
        nameof(OutcomeKind.Ok) => Outcome.Ok(new Answer(true)).Untyped,
        nameof(OutcomeKind.NoContent) => Outcome.NoContent(),
        nameof(OutcomeKind.Invalid) => Outcome.Invalid("bad"),
        nameof(OutcomeKind.Unauthorized) => Outcome.Unauthorized(),
        nameof(OutcomeKind.Forbidden) => Outcome.Forbidden("locked"),
        nameof(OutcomeKind.NotFound) => Outcome.NotFound(),
        nameof(OutcomeKind.Conflict) => Outcome.Conflict("state"),
        nameof(OutcomeKind.TooManyRequests) => Outcome.TooManyRequests(new RetryLaterResponse { Error = "wait", RetryAfterSeconds = 30 }),
        nameof(OutcomeKind.Unavailable) => Outcome.Unavailable(),
        nameof(OutcomeKind.Redirect) => Outcome.Redirect("/somewhere"),
        nameof(OutcomeKind.File) => Outcome.Bytes([1, 2, 3], "image/png"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Every_kind_answers_with_its_status(string kind, int status)
    {
        var ctx = await AnswerAsync(Of(kind).ToResult());
        Assert.Equal(status, ctx.Response.StatusCode);
    }

    [Fact]
    public void Every_kind_has_a_case_in_the_test_and_an_answer_at_the_edge()
    {
        var tested = Kinds.Select(row => (string)row[0]).ToHashSet();
        foreach (var kind in Enum.GetValues<OutcomeKind>())
        {
            Assert.Contains(kind.ToString(), tested);
            Assert.NotNull(Of(kind.ToString()).ToResult());
        }
    }

    [Fact]
    public async Task A_refusal_carries_its_body_as_json()
    {
        var forbidden = await AnswerAsync(Outcome.Forbidden("locked").ToResult());
        Assert.StartsWith("application/json", forbidden.Response.ContentType);
        Assert.Equal("locked", JsonDocument.Parse(BodyOf(forbidden)).RootElement.GetProperty("error").GetString());

        var tooSoon = await AnswerAsync(Outcome.TooManyRequests(new RetryLaterResponse { Error = "wait", RetryAfterSeconds = 30 }).ToResult());
        Assert.Equal(30, JsonDocument.Parse(BodyOf(tooSoon)).RootElement.GetProperty("retryAfterSeconds").GetInt32());

        // A bare 404 stays bare, as it always was.
        var missing = await AnswerAsync(Outcome.NotFound().ToResult());
        Assert.Equal("", BodyOf(missing));
    }

    [Fact]
    public async Task A_named_answer_reaches_the_wire_in_camel_case_in_declaration_order()
    {
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var slot = await AnswerAsync(Outcome.Ok(new UploadSlotResponse { Id = id, FileName = "brief.pdf" }).ToResult());
        Assert.Equal(200, slot.Response.StatusCode);
        Assert.Equal($"{{\"id\":\"{id}\",\"fileName\":\"brief.pdf\"}}", BodyOf(slot));

        // A union's answer is written as what it is, with no discriminator:
        // the base names the choice for the signature, the wire never sees it.
        var none = await AnswerAsync(Outcome.Ok<AiArtifactResponse>(new AiArtifactNone { Status = "none" }).ToResult());
        Assert.Equal("{\"status\":\"none\"}", BodyOf(none));
    }

    [Fact]
    public async Task An_answer_the_edge_makes_itself_is_named_under_the_same_rules()
    {
        // A gate writes straight to the response, as the middleware does,
        // and its flag rides beside the sentence.
        var gate = Context();
        await gate.Response.WriteAsJsonAsync(
            new TermsPendingResponse { Error = "Accept the current terms of service to continue.", TermsPending = true });
        Assert.Equal("{\"error\":\"Accept the current terms of service to continue.\",\"termsPending\":true}", BodyOf(gate));

        // A connection test the daily ceiling stopped never ran, so its
        // answer has no last test at all; one that ran says null when none was kept.
        var notRun = await AnswerAsync(
            Results.Ok(new SetupTestNotRunResponse { Ok = false, Detail = "The daily call ceiling is reached." }));
        Assert.Equal("{\"ok\":false,\"detail\":\"The daily call ceiling is reached.\"}", BodyOf(notRun));
        var ran = await AnswerAsync(Results.Ok(new SetupTestResponse { Ok = false, Detail = "No key.", Last = null }));
        Assert.Equal("{\"ok\":false,\"detail\":\"No key.\",\"last\":null}", BodyOf(ran));
    }

    [Fact]
    public void A_typed_outcome_carries_its_answer_and_takes_a_refusal_as_it_is()
    {
        Outcome<RatingResponse> Rate(bool paid) => paid
            ? Outcome.Ok(new RatingResponse { Stars = 5, Comment = null, UpdatedAtUtc = DateTimeOffset.UnixEpoch })
            : Outcome.Conflict("Ratings open once the award is marked paid.");

        var saved = Rate(paid: true);
        Assert.Equal(OutcomeKind.Ok, saved.Kind);
        Assert.Equal(5, saved.Body!.Stars);

        var refused = Rate(paid: false);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Null(refused.Body);
        Assert.Equal("Ratings open once the award is marked paid.", Assert.IsType<ErrorResponse>(refused.Untyped.Body).Error);
    }

    /// <summary>
    /// The serializer writes a derived type's own members before its base's,
    /// and writes a property by its declared type: a union base with members
    /// would reorder its answers, and a property typed as a base would drop
    /// the members of what it holds. So a base carries nothing, and no
    /// response property is typed as one.
    /// </summary>
    [Fact]
    public void A_response_union_base_carries_no_members_and_types_no_property()
    {
        // Both tiers answer: the services' records and the edge's own.
        var types = typeof(Outcome).Assembly.GetTypes().Concat(typeof(OutcomeResults).Assembly.GetTypes()).ToList();
        var bases = types
            .Where(t => t.IsAbstract && !t.IsSealed && t.GetMethod("<Clone>$") is not null)
            .ToList();
        Assert.Contains(typeof(AiArtifactResponse), bases);
        foreach (var b in bases)
            Assert.Empty(b.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        var typedAsBase = types
            .SelectMany(t => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(p => bases.Contains(p.PropertyType)
                || (p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(bases.Contains)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}");
        Assert.Empty(typedAsBase);
    }

    [Fact]
    public async Task A_file_keeps_its_type_and_version()
    {
        var modified = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var ctx = await AnswerAsync(Outcome.Bytes([7, 8, 9], "image/webp", modified, "\"1788264000\"").ToResult());
        Assert.Equal("image/webp", ctx.Response.ContentType);
        Assert.Equal("\"1788264000\"", ctx.Response.Headers.ETag.ToString());
        Assert.Equal(modified.ToString("R"), ctx.Response.Headers.LastModified.ToString());
    }

    [Fact]
    public async Task A_signed_out_browser_link_lands_on_the_login_page_and_comes_back()
    {
        var ctx = Context();
        ctx.Request.Path = "/api/attachments/6f9619ff-8b86-d011-b42d-00c04fc964ff/download";
        await Outcome.Unauthorized().ToBrowserResult(ctx.Request).ExecuteAsync(ctx);
        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Equal("/login?next=%2Fapi%2Fattachments%2F6f9619ff-8b86-d011-b42d-00c04fc964ff%2Fdownload", ctx.Response.Headers.Location.ToString());

        // Anything else answers as it would anywhere.
        var missing = Context();
        await Outcome.NotFound().ToBrowserResult(missing.Request).ExecuteAsync(missing);
        Assert.Equal(404, missing.Response.StatusCode);
    }

    // ------------------------------------------------------------ session

    private static readonly TokenService Tokens = new(JwtConfig.WithKey(RandomNumberGenerator.GetBytes(32)));

    private static User Someone() => new()
    {
        Id = Guid.NewGuid(),
        Email = "someone@example.com",
        DisplayName = "Someone",
        PasswordHash = "x",
        Role = Roles.Freelancer,
    };

    private static string? SessionCookieOf(HttpContext ctx) =>
        ctx.Response.Headers.SetCookie.FirstOrDefault(c => c!.StartsWith(SessionCookie.Name + "="));

    [Fact]
    public void A_sign_in_starts_a_session_and_keep_me_signed_in_outlives_the_browser()
    {
        var once = Context();
        SessionCookie.Apply(once, Tokens, Outcome.Ok(new Answer(true)).WithSignIn(Someone(), persistent: false));
        Assert.NotNull(SessionCookieOf(once));
        Assert.DoesNotContain("expires=", SessionCookieOf(once)!, StringComparison.OrdinalIgnoreCase);

        var kept = Context();
        SessionCookie.Apply(kept, Tokens, Outcome.Ok(new Answer(true)).WithSignIn(Someone(), persistent: true));
        Assert.Contains("expires=", SessionCookieOf(kept)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void On_two_hostnames_the_cookie_is_scoped_to_the_parent_they_share_and_cleared_the_same_way()
    {
        // The pages at web.crm.com, the API at api.crm.com: the cookie the
        // sign-in sets must reach the web server too, so it carries crm.com;
        // and only a delete naming the same domain removes it.
        var origin = WebOrigin.FromSettings("https://web.crm.com", "https://api.crm.com");
        var split = Context();
        split.Items[Origins.ItemKey] = origin;
        SessionCookie.Apply(split, Tokens, Outcome.Ok(new Answer(true)).WithSignIn(Someone(), persistent: false));
        Assert.Contains("domain=crm.com", SessionCookieOf(split)!, StringComparison.OrdinalIgnoreCase);

        var cleared = Context();
        cleared.Items[Origins.ItemKey] = origin;
        SessionCookie.Clear(cleared);
        Assert.Contains("domain=crm.com", SessionCookieOf(cleared)!, StringComparison.OrdinalIgnoreCase);

        // Same-origin: host-only, as before.
        var same = Context();
        SessionCookie.Apply(same, Tokens, Outcome.Ok(new Answer(true)).WithSignIn(Someone(), persistent: false));
        Assert.DoesNotContain("domain=", SessionCookieOf(same)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_changed_account_re_issues_only_a_cookie_session_on_its_own_choice()
    {
        var user = Someone();

        // A bearer client picks the change up at its next refresh.
        var bearer = Context();
        bearer.User = new ClaimsPrincipal(Principal.Identity(user, persistent: true));
        SessionCookie.Apply(bearer, Tokens, Outcome.NoContent().WithRefreshed(user));
        Assert.Null(SessionCookieOf(bearer));

        var browser = Context();
        browser.Items[SessionValidation.ViaCookieItem] = true;
        browser.User = new ClaimsPrincipal(Principal.Identity(user, persistent: true));
        SessionCookie.Apply(browser, Tokens, Outcome.NoContent().WithRefreshed(user));
        Assert.Contains("expires=", SessionCookieOf(browser)!, StringComparison.OrdinalIgnoreCase);

        // An outcome that says nothing about the session writes nothing.
        var quiet = Context();
        quiet.Items[SessionValidation.ViaCookieItem] = true;
        SessionCookie.Apply(quiet, Tokens, Outcome.Ok(new Answer(true)));
        Assert.Null(SessionCookieOf(quiet));
    }

    [Fact]
    public void A_session_note_survives_on_the_copy_and_leaves_the_original_alone()
    {
        var plain = Outcome.Ok(new Answer(true));
        var signedIn = plain.WithSignIn(Someone(), persistent: true);
        Assert.Null(plain.Untyped.SignIn);
        Assert.Same(plain.Body, signedIn.Body);
        Assert.Equal(OutcomeKind.Ok, signedIn.Kind);
        Assert.True(signedIn.Untyped.SignIn!.Persistent);
    }
}
