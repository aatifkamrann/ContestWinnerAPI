namespace WinnersPortal.Services.Common;

/// <summary>
/// The two facts about a request the pure gate rules need, without the web
/// framework's types: whether the method reads, and whether a path sits
/// under a prefix segment-wise ("/api/auth" matches "/api/auth/me", never
/// "/api/authors").
/// </summary>
public static class Requests
{
    public static bool IsRead(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    public static bool StartsWithSegments(string path, string prefix) =>
        path.Length >= prefix.Length
        && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
