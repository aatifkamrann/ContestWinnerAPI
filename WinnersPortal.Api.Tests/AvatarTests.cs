using WinnersPortal.Services.Profiles;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The profile picture's rules. The one property worth pinning is that
/// the bytes decide what they are: a declared content type is the part of
/// an upload that costs nothing to lie about, so nothing here reads one.
/// </summary>
public class AvatarTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Webp =
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x10, 0x00, 0x00, 0x00, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 0x56];

    [Fact]
    public void The_bytes_say_what_they_are()
    {
        Assert.Equal("image/jpeg", AvatarRules.SniffContentType(Jpeg));
        Assert.Equal("image/png", AvatarRules.SniffContentType(Png));
        Assert.Equal("image/webp", AvatarRules.SniffContentType(Webp));
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]          // GIF: a picture, but not one the portal serves
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x20 })]                // "<svg " — scriptable, never
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 })]                      // %PDF
    [InlineData(new byte[] { 0xFF, 0xD8 })]                                  // too short to be sure
    public void Anything_else_is_not_a_picture_here(byte[] bytes)
    {
        Assert.Null(AvatarRules.SniffContentType(bytes));
    }

    [Fact]
    public void A_data_url_from_a_canvas_is_read_by_its_bytes_not_its_label()
    {
        // The label says PNG; the bytes are a JPEG. The bytes win.
        var sent = "data:image/png;base64," + Convert.ToBase64String(Jpeg);
        var (bytes, contentType, problem) = AvatarRules.Read(sent);
        Assert.Null(problem);
        Assert.Equal("image/jpeg", contentType);
        Assert.Equal(Jpeg, bytes);
    }

    [Fact]
    public void Bare_base64_is_read_too()
    {
        var (bytes, contentType, problem) = AvatarRules.Read(Convert.ToBase64String(Png));
        Assert.Null(problem);
        Assert.Equal("image/png", contentType);
        Assert.Equal(Png, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_sent_is_said_plainly(string? sent)
    {
        var (bytes, _, problem) = AvatarRules.Read(sent);
        Assert.Null(bytes);
        Assert.Equal("Choose a picture first.", problem);
    }

    [Fact]
    public void Text_that_is_not_base64_is_refused_rather_than_stored()
    {
        var (bytes, _, problem) = AvatarRules.Read("not base64 at all!");
        Assert.Null(bytes);
        Assert.Contains("could not be read", problem);
    }

    [Fact]
    public void A_picture_in_the_wrong_format_is_named_as_such()
    {
        var gif = Convert.ToBase64String(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00 });
        var (bytes, _, problem) = AvatarRules.Read(gif);
        Assert.Null(bytes);
        Assert.Contains("JPEG, PNG or WebP", problem);
    }

    [Fact]
    public void An_oversize_upload_is_refused_before_it_is_decoded()
    {
        // Encoded length alone says it is too big: no decode is attempted,
        // so the refusal costs the same whatever the size.
        var oversize = new string('A', (AvatarRules.MaxBytes / 3 + 2) * 4);
        var (bytes, _, problem) = AvatarRules.Read(oversize);
        Assert.Null(bytes);
        Assert.Equal(AvatarRules.TooBig, problem);
    }

    [Fact]
    public void A_picture_just_over_the_ceiling_is_refused_after_decoding_too()
    {
        var big = new byte[AvatarRules.MaxBytes + 1];
        Jpeg.CopyTo(big, 0);
        var (bytes, _, problem) = AvatarRules.Read(Convert.ToBase64String(big));
        Assert.Null(bytes);
        Assert.Equal(AvatarRules.TooBig, problem);
    }

    [Fact]
    public void The_url_is_versioned_by_when_the_picture_was_set_and_absent_without_one()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var at = DateTimeOffset.Parse("2026-09-11T10:00:00Z");
        Assert.Equal($"/api/avatars/{id}?v={at.ToUnixTimeSeconds()}", AvatarRules.Url(id, at));
        Assert.Null(AvatarRules.Url(id, null));
        // A new picture is a new address, which is what lets the old one be
        // cached for a year without ever being shown by mistake.
        Assert.NotEqual(AvatarRules.Url(id, at), AvatarRules.Url(id, at.AddSeconds(1)));
    }
}
