using System.Text.Json;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Settings;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The gateways the portal ships knowing how to talk to. These are the
/// tests that keep a preset honest: an operator picks a name from a list
/// and gives it a key, so a preset with the wrong placeholder in it fails
/// as a code that never arrives — for everybody, silently, until somebody
/// complains they cannot sign up.
/// </summary>
public class PhoneGatewayTests
{
    [Fact]
    public void Every_preset_says_where_to_send_and_what_to_say()
    {
        Assert.NotEmpty(PhoneProviders.All);
        foreach (var p in PhoneProviders.All)
        {
            Assert.True(PhoneSender.Configured(p.Url), $"{p.Key}: {p.Url} is not an http address");
            Assert.StartsWith("https://", p.Url); // a code is a secret in flight
            Assert.Contains("{to}", p.Body);
            Assert.Contains("{text}", p.Body);
            Assert.False(string.IsNullOrWhiteSpace(p.Label), $"{p.Key} has no label");
        }
    }

    [Fact]
    public void Every_preset_carries_the_key_somewhere()
    {
        // A gateway that takes the key in neither a header nor the body
        // would be called anonymously and refuse every message.
        foreach (var p in PhoneProviders.All)
            Assert.True(p.AuthHeader is not null || p.Body.Contains("{key}"),
                $"{p.Key} sends the API key nowhere");
    }

    [Fact]
    public void A_preset_asks_for_a_sender_exactly_when_it_uses_one()
    {
        // The settings field is only worth filling in for the providers
        // that have a notion of one — and a provider whose body wants a
        // {from} must say so, or the operator leaves it blank and the
        // gateway refuses a message with no sender.
        foreach (var p in PhoneProviders.All)
            Assert.Equal(p.Body.Contains("{from}"), p.Sender is not null);
    }

    [Fact]
    public void Preset_keys_are_unique_and_never_the_two_reserved_words()
    {
        var keys = PhoneProviders.All.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(PhoneProviders.None, keys);
        Assert.DoesNotContain(PhoneProviders.Custom, keys);
    }

    [Fact]
    public void The_dropdown_opens_on_off_and_ends_on_custom()
    {
        var values = PhoneProviders.Choices.Select(c => c.Value).ToList();
        Assert.Equal(PhoneProviders.None, values[0]);
        Assert.Equal(PhoneProviders.Custom, values[^1]);
        foreach (var p in PhoneProviders.All) Assert.Contains(p.Key, values);
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PhoneProviders.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
    }

    [Theory]
    [InlineData("textbee")]
    [InlineData("httpsms")]
    [InlineData("sendpk")]
    [InlineData("telnyx")]
    public void A_named_provider_is_found_however_it_was_cased(string key)
    {
        Assert.NotNull(PhoneProviders.Find(key));
        Assert.NotNull(PhoneProviders.Find(key.ToUpperInvariant()));
        Assert.NotNull(PhoneProviders.Find($"  {key} "));
    }

    [Theory]
    [InlineData(PhoneProviders.None)]
    [InlineData(PhoneProviders.Custom)]
    [InlineData("twilio")] // a name the portal has no preset for
    [InlineData("")]
    [InlineData(null)]
    public void Off_custom_and_the_unknown_resolve_to_no_preset(string? key) =>
        Assert.Null(PhoneProviders.Find(key));

    // ------------------------------------------------------- the request

    [Fact]
    public void A_json_provider_composes_the_document_it_documented()
    {
        var textbee = PhoneProviders.Find("textbee")!;
        var body = PhoneSender.Body(textbee.Body, textbee.Format, "+923001234567", "480507 is your code.", "", "k");
        Assert.Equal("""{"recipients":["+923001234567"],"message":"480507 is your code."}""", body);
    }

    [Fact]
    public void A_form_provider_composes_form_fields_with_its_key_inside_them()
    {
        var sendpk = PhoneProviders.Find("sendpk")!;
        var body = PhoneSender.Body(sendpk.Body, sendpk.Format, "+923001234567", "480507 is your code.", "AstrikWP", "secret-key");
        Assert.Equal(
            "api_key=secret-key&sender=AstrikWP&mobile=%2B923001234567&message=480507%20is%20your%20code.",
            body);
    }

    [Fact]
    public void No_value_can_break_out_of_its_field_in_either_format()
    {
        // The message is the portal's own and tame, but the sender ID and
        // the key are typed by an operator, and a stray & or " must not
        // rewrite the request around them.
        var form = PhoneSender.Body("api_key={key}&sender={from}&mobile={to}&message={text}",
            GatewayFormat.Form, "+1", "hi", "A&B=C", "k&evil=1");
        Assert.Equal("api_key=k%26evil%3D1&sender=A%26B%3DC&mobile=%2B1&message=hi", form);
        // Parsed back, the injected pairs are values, not fields.
        var fields = form.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal(4, fields.Count);
        Assert.Equal("k&evil=1", fields["api_key"]);

        var json = PhoneSender.Body("""{"to":"{to}","content":"{text}"}""",
            GatewayFormat.Json, "+1", "say \"hi\"", "", "");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("say \"hi\"", doc.RootElement.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("https://api.textbee.dev/api/v1/gateway/send-sms", true)]
    [InlineData("http://192.168.1.20:8080/send", true)]
    [InlineData(" https://api.httpsms.com/v1/messages/send ", true)]
    [InlineData("api.textbee.dev/send", false)]
    [InlineData("ftp://x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_custom_gateway_needs_an_http_address(string? url, bool configured) =>
        Assert.Equal(configured, PhoneSender.Configured(url));

    [Fact]
    public void A_preset_never_sends_its_key_in_two_places_at_once()
    {
        // The key goes where the provider asked for it: a header, or a
        // {key} in the body — never both, which would hand a secret to a
        // gateway in a position it never asked for.
        foreach (var p in PhoneProviders.All)
            Assert.False(p.AuthHeader is not null && p.Body.Contains("{key}"),
                $"{p.Key} sends the API key twice");
    }

    // ------------------------------------------------ a 200 that means no

    [Theory]
    [InlineData("ERR: invalid api_key")]
    [InlineData("ERROR 101")]
    [InlineData("FAILED — sender not registered")]
    [InlineData("Invalid mobile number")]
    [InlineData("""{"success":false,"message":"no balance"}""")]
    [InlineData("""{ "success": false }""")]
    [InlineData("""{"status":"error","code":7}""")]
    public void A_gateway_that_answers_200_and_refuses_is_read_as_a_refusal(string body) =>
        Assert.True(PhoneSender.LooksLikeRefusal(body));

    [Theory]
    [InlineData("OK ID:29346")]
    [InlineData("""{"success":true,"id":"abc"}""")]
    [InlineData("""{"data":{"id":"ERR123"},"errors":[]}""")] // an id with the letters in it is not a refusal
    [InlineData("""{"messages":[{"status":"0"}]}""")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_gateway_that_accepted_is_not_second_guessed(string body) =>
        Assert.False(PhoneSender.LooksLikeRefusal(body));

    // ------------------------------------------------------- the settings

    [Fact]
    public void The_provider_setting_offers_exactly_what_the_registry_knows()
    {
        var setting = SettingsRegistry.Find(PhoneSender.ProviderKey);
        Assert.NotNull(setting);
        Assert.NotNull(setting!.Choices);
        Assert.Equal(
            PhoneProviders.Choices.Select(c => c.Value),
            setting.Choices!.Select(c => c.Value));
        // Off by default: a fresh portal promises nobody a text it cannot send.
        Assert.Equal(PhoneProviders.None, setting.Default);
    }

    [Fact]
    public void The_key_is_the_only_secret_in_the_group()
    {
        var group = SettingsRegistry.All.Where(s => s.Group == "phone").ToList();
        Assert.Equal([PhoneSender.ApiKeyKey], group.Where(s => s.IsSecret).Select(s => s.Key));
        // Everything in the group is worth explaining — none of it is guessable.
        Assert.All(group, s => Assert.True(s.HelpRequired, $"{s.Key} has no help"));
    }
}
