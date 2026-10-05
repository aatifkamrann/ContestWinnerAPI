using WinnersPortal.Api.Reports;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Domain;
using WinnersPortal.Services.Profiles;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// Where an award goes. Two things are being pinned here and they fail very
/// differently: a broken registry is a client copying an account number that
/// is missing half of itself, and a broken visibility rule is one
/// freelancer reading another one's bank details. The second has no error
/// message and nobody would notice it.
/// </summary>
public class PaymentMethodTests
{
    // ---------------------------------------------------------- who reads it

    [Fact]
    public void Only_the_client_who_awarded_them_reads_it_besides_the_owner_and_administrators()
    {
        Assert.True(ProfileRules.MayReadPayments(own: true, Roles.Freelancer, awardedByViewer: false));
        Assert.True(ProfileRules.MayReadPayments(own: false, Roles.Admin, awardedByViewer: false));
        Assert.True(ProfileRules.MayReadPayments(own: false, Roles.Client, awardedByViewer: true));

        // Being a client is not the reason; the award is.
        Assert.False(ProfileRules.MayReadPayments(own: false, Roles.Client, awardedByViewer: false));
        Assert.False(ProfileRules.MayReadPayments(own: false, Roles.Freelancer, awardedByViewer: false));
        // Only a client awards, so a freelancer claiming one is still a rival.
        Assert.False(ProfileRules.MayReadPayments(own: false, Roles.Freelancer, awardedByViewer: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Client")]     // the claim is stored lower-cased; this is not it
    [InlineData("administrator")]
    [InlineData("freelancer ")]
    public void A_role_that_is_not_exactly_one_of_ours_reads_nothing(string? role) =>
        Assert.False(ProfileRules.MayReadPayments(own: false, role, awardedByViewer: true));

    [Theory]
    [InlineData(Roles.Freelancer, true)]
    [InlineData(Roles.Client, false)]      // never owed an award
    [InlineData(Roles.Admin, false)]
    [InlineData(null, false)]
    public void Only_a_freelancer_profile_has_payment_details(string? accountRole, bool expected) =>
        Assert.Equal(expected, ProfileRules.HasPayments(accountRole));

    // ------------------------------------------------------------ the registry

    [Fact]
    public void Every_method_asks_for_something_and_knows_which_answer_names_the_account()
    {
        Assert.NotEmpty(PaymentMethods.All);
        foreach (var m in PaymentMethods.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Label), $"{m.Key} has no label");
            Assert.False(string.IsNullOrWhiteSpace(m.Hint), $"{m.Key} has no hint");
            Assert.NotEmpty(m.Fields);

            // The identifying field has to be one this method actually asks
            // for, and asks for always — a duplicate check against a field
            // that may be blank would let the same account in twice.
            var identity = m.Fields.SingleOrDefault(f => f.Key == m.Identity);
            Assert.True(identity is not null, $"{m.Key} identifies itself by a field it never asks for");
            Assert.True(identity!.Required, $"{m.Key} identifies itself by an optional field");

            foreach (var f in m.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Label), $"{m.Key}.{f.Key} has no label");
                Assert.False(string.IsNullOrWhiteSpace(f.Placeholder), $"{m.Key}.{f.Key} has no example");
                Assert.InRange(f.MaxLength, 1, PaymentMethods.MaxFieldValue);
            }
            Assert.Equal(m.Fields.Count, m.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        }
        Assert.Equal(
            PaymentMethods.All.Count,
            PaymentMethods.All.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_bank_row_cannot_be_saved_without_the_number_a_client_has_to_type()
    {
        var bank = PaymentMethods.Find("bank")!;
        Assert.Contains(bank.Fields, f => f.Key == "holder" && f.Required);
        Assert.Contains(bank.Fields, f => f.Key == "number" && f.Required);
        // Only needed crossing a border, so never a reason to refuse a save.
        Assert.Contains(bank.Fields, f => f.Key == "swift" && !f.Required);
    }

    [Fact]
    public void A_crypto_row_asks_for_the_network_as_well_as_the_address()
    {
        // The failure this prevents costs the whole award: an address that
        // is perfectly valid, on a chain the coin was never sent over.
        var crypto = PaymentMethods.Find("crypto")!;
        Assert.Contains(crypto.Fields, f => f.Key == "coin" && f.Required);
        Assert.Equal("address", crypto.Identity);
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("BANK")]
    [InlineData("  wallet ")]
    public void A_method_is_found_however_it_was_cased(string key) =>
        Assert.NotNull(PaymentMethods.Find(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("stripe")]
    public void A_method_this_portal_never_offered_is_no_method(string? key) =>
        Assert.Null(PaymentMethods.Find(key));

    // ------------------------------------------------------------- the fields

    [Fact]
    public void Only_the_fields_the_method_asked_for_are_kept()
    {
        var paypal = PaymentMethods.Find("paypal")!;
        var cleaned = PaymentMethods.CleanDetails(paypal, new Dictionary<string, string?>
        {
            ["account"] = "  you@example.com  ",
            // A field from a different method, or from nowhere at all. Kept,
            // it would be a value nobody renders and nobody reviewed.
            ["number"] = "PK36SCBL0000001123456702",
            ["password"] = "hunter2",
        });

        Assert.Equal(new[] { "account" }, cleaned.Keys);
        Assert.Equal("you@example.com", cleaned["account"]);
    }

    [Fact]
    public void A_blank_field_is_no_answer_and_a_long_one_is_cut_to_its_limit()
    {
        var bank = PaymentMethods.Find("bank")!;
        var cleaned = PaymentMethods.CleanDetails(bank, new Dictionary<string, string?>
        {
            ["holder"] = new string('a', 500),
            ["bank"] = "   ",
            ["number"] = "123",
            ["swift"] = null,
        });

        Assert.Equal(bank.Fields.Single(f => f.Key == "holder").MaxLength, cleaned["holder"].Length);
        Assert.DoesNotContain("bank", cleaned.Keys);
        Assert.DoesNotContain("swift", cleaned.Keys);
    }

    [Fact]
    public void Nothing_sent_at_all_is_an_empty_row_rather_than_a_crash() =>
        Assert.Empty(PaymentMethods.CleanDetails(PaymentMethods.Find("bank")!, null));

    // -------------------------------------------------------------- the rules

    [Fact]
    public void A_row_missing_what_the_client_has_to_type_is_refused_by_name()
    {
        var bank = PaymentMethods.Find("bank")!;
        var problem = PaymentMethods.Problem(
        [
            (bank, new Dictionary<string, string> { ["holder"] = "A Person", ["bank"] = "HBL" }),
        ]);

        Assert.NotNull(problem);
        Assert.Contains("Bank transfer", problem);
        Assert.Contains("account number", problem);
    }

    [Fact]
    public void The_same_account_written_two_ways_is_still_one_account()
    {
        var bank = PaymentMethods.Find("bank")!;
        Dictionary<string, string> Row(string number) => new()
        {
            ["holder"] = "A Person",
            ["bank"] = "HBL",
            ["number"] = number,
        };

        // Spacing and case are how people actually write an IBAN; neither
        // makes it a second account.
        Assert.Contains("twice", PaymentMethods.Problem(
            [(bank, Row("PK36 SCBL 0000 0011 2345 6702")), (bank, Row("pk36scbl0000001123456702"))])!);
        Assert.Contains("twice", PaymentMethods.Problem(
            [(bank, Row("1234-5678-9")), (bank, Row("123456789"))])!);

        // Two real accounts at the same bank are two rows, and fine.
        Assert.Null(PaymentMethods.Problem([(bank, Row("123456789")), (bank, Row("987654321"))]));
    }

    [Fact]
    public void The_same_number_at_two_different_services_is_two_accounts()
    {
        // A phone number is both a JazzCash wallet and a Western Union
        // contact. Folding those together would refuse a save that is right.
        var wallet = PaymentMethods.Find("wallet")!;
        var remittance = PaymentMethods.Find("remittance")!;
        Assert.Null(PaymentMethods.Problem(
        [
            (wallet, new Dictionary<string, string>
            {
                ["provider"] = "JazzCash", ["holder"] = "A Person", ["number"] = "+923001234567",
            }),
            (remittance, new Dictionary<string, string>
            {
                ["service"] = "Western Union", ["name"] = "A Person",
                ["place"] = "Lahore, Pakistan", ["phone"] = "+923001234567",
            }),
        ]));
    }

    [Fact]
    public void A_filing_cabinet_of_accounts_is_refused()
    {
        var paypal = PaymentMethods.Find("paypal")!;
        var rows = Enumerable.Range(0, ProfileRules.MaxPayments + 1)
            .Select(i => (paypal, new Dictionary<string, string> { ["account"] = $"a{i}@example.com" }))
            .ToList();

        Assert.Contains("At most", PaymentMethods.Problem(rows)!);
    }

    [Fact]
    public void A_complete_set_of_ways_to_be_paid_is_accepted()
    {
        // Every method the portal ships, filled in the way the form would
        // fill it, all at once — the shape a real profile takes.
        var rows = PaymentMethods.All
            .Take(ProfileRules.MaxPayments)
            .Select(m => (m, m.Fields
                .Where(f => f.Required)
                .ToDictionary(f => f.Key, f => $"{m.Key}-{f.Key}", StringComparer.Ordinal)))
            .ToList();

        Assert.Null(PaymentMethods.Problem(rows));
    }
}
