using WinnersPortal.Domain;

namespace WinnersPortal.Services.Common;

/// <summary>What a service call came to, in the business tier's words for it.</summary>
public enum OutcomeKind
{
    /// <summary>Done; <see cref="Outcome.Body"/> is the answer, when there is one.</summary>
    Ok,
    /// <summary>Done, with nothing to say back.</summary>
    NoContent,
    /// <summary>The request itself is wrong; the body says how.</summary>
    Invalid,
    /// <summary>Nobody is signed in, the credentials are wrong, or the session names an account that is gone.</summary>
    Unauthorized,
    /// <summary>Signed in, but this account may not; the body says why.</summary>
    Forbidden,
    /// <summary>No such thing — or none this caller may learn exists.</summary>
    NotFound,
    /// <summary>The request is fine; the state it meets is not.</summary>
    Conflict,
    /// <summary>Asked again too soon; the body carries the wait.</summary>
    TooManyRequests,
    /// <summary>Something the portal depends on is not configured.</summary>
    Unavailable,
    /// <summary>The answer lives at <see cref="Outcome.Location"/>.</summary>
    Redirect,
    /// <summary>The answer is bytes: <see cref="Outcome.Content"/>.</summary>
    File,
}

/// <summary>
/// The answer a service gives the edge. Nothing here knows HTTP: the API turns
/// the kind into a status code (<c>OutcomeResults.ToResult</c>), and the same
/// call reads the same to a test. A success with something to say is an
/// <see cref="Outcome{T}"/>, which names the response type; a refusal says
/// why in an <see cref="IErrorResponse"/>.
/// </summary>
public sealed class Outcome
{
    private Outcome(OutcomeKind kind, object? body = null)
    {
        Kind = kind;
        Body = body;
    }

    public OutcomeKind Kind { get; }

    /// <summary>What is sent back: the answer, or the error the form shows.</summary>
    public object? Body { get; }

    /// <summary>Where a <see cref="OutcomeKind.Redirect"/> sends the browser.</summary>
    public string? Location { get; private set; }

    /// <summary>A <see cref="OutcomeKind.File"/>'s bytes, and how to read them.</summary>
    public byte[]? Content { get; private set; }
    public string? ContentType { get; private set; }
    public DateTimeOffset? LastModified { get; private set; }

    /// <summary>A quoted entity tag for the bytes, when they have a version.</summary>
    public string? ETag { get; private set; }

    /// <summary>
    /// A session to start for this account — a sign-in, a registration, a
    /// password reset — and whether the browser keeps it once closed. The
    /// edge writes the cookie; no service sees one.
    /// </summary>
    public SignIn? SignIn { get; private set; }

    /// <summary>
    /// The signed-in account's own claims changed (its name, its password
    /// stamp): a session the browser carries in a cookie is re-issued to keep
    /// up, on the keep-me-signed-in choice it already had.
    /// </summary>
    public User? Refreshed { get; private set; }

    public static Outcome<T> Ok<T>(T body) => new(new Outcome(OutcomeKind.Ok, body));
    public static Outcome NoContent() => new(OutcomeKind.NoContent);
    public static Outcome Invalid(string error) => Invalid(new ErrorResponse(error));
    public static Outcome Invalid(IErrorResponse body) => new(OutcomeKind.Invalid, body);
    public static Outcome Unauthorized() => new(OutcomeKind.Unauthorized);
    public static Outcome Forbidden(string error) => new(OutcomeKind.Forbidden, new ErrorResponse(error));
    public static Outcome Forbidden(IErrorResponse body) => new(OutcomeKind.Forbidden, body);
    public static Outcome NotFound() => new(OutcomeKind.NotFound);
    public static Outcome Conflict(string error) => new(OutcomeKind.Conflict, new ErrorResponse(error));
    public static Outcome TooManyRequests(IErrorResponse body) => new(OutcomeKind.TooManyRequests, body);
    public static Outcome Unavailable() => new(OutcomeKind.Unavailable);
    public static Outcome Redirect(string location) => new(OutcomeKind.Redirect) { Location = location };

    public static Outcome Bytes(byte[] content, string? contentType = null, DateTimeOffset? lastModified = null, string? etag = null) =>
        new(OutcomeKind.File) { Content = content, ContentType = contentType, LastModified = lastModified, ETag = etag };

    public Outcome WithSignIn(User account, bool persistent)
    {
        var copy = (Outcome)MemberwiseClone();
        copy.SignIn = new SignIn(account, persistent);
        return copy;
    }

    public Outcome WithRefreshed(User account)
    {
        var copy = (Outcome)MemberwiseClone();
        copy.Refreshed = account;
        return copy;
    }
}

/// <summary>
/// An outcome that names its answer: what a service method returns when
/// success has something to say. <typeparamref name="T"/> is the response the
/// wire carries; a refusal, which has nothing of that shape to say, converts
/// from a plain <see cref="Outcome"/>.
/// </summary>
public sealed class Outcome<T>
{
    internal Outcome(Outcome untyped) => Untyped = untyped;

    /// <summary>The same outcome as the edge reads it, the answer in <see cref="Outcome.Body"/>.</summary>
    public Outcome Untyped { get; }

    public OutcomeKind Kind => Untyped.Kind;

    /// <summary>The answer, when the kind is <see cref="OutcomeKind.Ok"/>.</summary>
    public T? Body => Untyped.Body is T body ? body : default;

    public static implicit operator Outcome<T>(Outcome outcome) => new(outcome);

    public Outcome<T> WithSignIn(User account, bool persistent) => new(Untyped.WithSignIn(account, persistent));

    public Outcome<T> WithRefreshed(User account) => new(Untyped.WithRefreshed(account));
}

/// <summary>What every refusal's body carries: the sentence the form shows.</summary>
public interface IErrorResponse
{
    string Error { get; }
}

/// <summary>A refusal with nothing to add to its sentence.</summary>
public sealed record ErrorResponse(string Error) : IErrorResponse;

/// <summary>An account to sign in, and whether the session outlives the browser.</summary>
public sealed record SignIn(User Account, bool Persistent);
