using System.Security.Cryptography;
using System.Text;

namespace WinnersPortal.Services.Storage;

/// <summary>
/// AWS Signature V4 query presigning, hand-rolled — like the GitHub App JWT,
/// a couple of HMAC chains and some strict URI encoding are not worth an SDK.
/// Path-style addressing (<c>endpoint/bucket/key</c>), which both MinIO and
/// R2 accept, and <c>UNSIGNED-PAYLOAD</c>, which is what makes one signature
/// good for a body the server never sees.
///
/// This is the portal's whole security model for reading files: the API
/// checks who is asking and signs a short-lived URL; the bytes then come to
/// the browser from the object store without crossing the app server. An
/// upload goes the other way round — through the API, which writes it with
/// the same signer — so a bucket is never asked to answer a page.
/// </summary>
public static class S3Presign
{
    /// <summary>
    /// A presigned URL for one request. <paramref name="bucket"/> may be
    /// empty when the endpoint already addresses the bucket (virtual-host
    /// style); <paramref name="extraQuery"/> carries response overrides such
    /// as <c>response-content-disposition</c>, which become part of what is
    /// signed — a holder of the link cannot change how the file comes back.
    /// </summary>
    public static Uri Url(
        Uri endpoint,
        string bucket,
        string key,
        string accessKey,
        string secretKey,
        string region,
        string method,
        TimeSpan expires,
        DateTimeOffset now,
        IEnumerable<KeyValuePair<string, string>>? extraQuery = null)
    {
        var amzDate = now.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd");
        var scope = $"{dateStamp}/{region}/s3/aws4_request";

        // Host header: the one signed header. Port only when non-default.
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";

        var canonicalUri = CanonicalPath(bucket, key);

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{accessKey}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = ((long)expires.TotalSeconds).ToString(),
            ["X-Amz-SignedHeaders"] = "host",
        };
        foreach (var (k, v) in extraQuery ?? []) query[k] = v;

        var canonicalQuery = string.Join("&",
            query.Select(kv => $"{Rfc3986(kv.Key)}={Rfc3986(kv.Value)}"));

        var canonicalRequest =
            $"{method}\n{canonicalUri}\n{canonicalQuery}\nhost:{host}\n\nhost\nUNSIGNED-PAYLOAD";

        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{HexSha256(canonicalRequest)}";

        // The V4 key derivation: date → region → service → "aws4_request".
        var signingKey = Hmac(Hmac(Hmac(Hmac(
            Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp), region), "s3"), "aws4_request");
        var signature = Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();

        var baseUrl = endpoint.GetLeftPart(UriPartial.Authority);
        return new Uri($"{baseUrl}{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}");
    }

    /// <summary>"/bucket/each/segment/encoded" — slashes kept, segments strictly encoded once.</summary>
    public static string CanonicalPath(string bucket, string key)
    {
        var parts = new List<string>();
        if (bucket.Length > 0) parts.Add(Rfc3986(bucket));
        parts.AddRange(key.Split('/').Select(Rfc3986));
        return "/" + string.Join("/", parts);
    }

    /// <summary>
    /// The encoding SigV4 is exact about: unreserved characters bare,
    /// everything else percent-encoded with uppercase hex, space as %20.
    /// Uri.EscapeDataString is close but tracks .NET's RFC table, not AWS's —
    /// spelling it out is what makes the signature reproducible.
    /// </summary>
    public static string Rfc3986(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(Convert.ToHexString([b]));
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string HexSha256(string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
}
