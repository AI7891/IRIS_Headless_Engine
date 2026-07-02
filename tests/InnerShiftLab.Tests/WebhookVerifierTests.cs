using System.Security.Cryptography;
using System.Text;
using InnerShiftLab.Auth;
using InnerShiftLab.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace InnerShiftLab.Tests;

public class WebhookVerifierTests
{
    private const string MetaSecret = "meta-secret-abc";
    private const string SkoolSecret = "skool-secret-xyz";

    private static WebhookVerifier NewVerifier() =>
        new(Options.Create(new MonetizationSettings { MetaAppSecret = MetaSecret, SkoolWebhookSecret = SkoolSecret }),
            NullLogger<WebhookVerifier>.Instance);

    private static string HmacHex(string secret, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    [Fact]
    public void MetaChallenge_ValidToken_ReturnsChallenge()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["hub.mode"] = "subscribe",
            ["hub.verify_token"] = MetaSecret,
            ["hub.challenge"] = "1234567890",
        });
        Assert.Equal("1234567890", NewVerifier().VerifyMetaChallenge(q));
    }

    [Fact]
    public void MetaChallenge_WrongToken_ReturnsNull()
    {
        var q = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["hub.mode"] = "subscribe",
            ["hub.verify_token"] = "wrong",
            ["hub.challenge"] = "1234567890",
        });
        Assert.Null(NewVerifier().VerifyMetaChallenge(q));
    }

    [Fact]
    public void MetaSignature_MatchingHmac_Verifies()
    {
        var body = "{\"event\":\"test\"}";
        var headers = new HeaderDictionary
        {
            ["X-Hub-Signature-256"] = "sha256=" + HmacHex(MetaSecret, body),
        };
        Assert.True(NewVerifier().VerifyMetaSignature(headers, body));
    }

    [Fact]
    public void MetaSignature_TamperedBody_Fails()
    {
        var headers = new HeaderDictionary
        {
            ["X-Hub-Signature-256"] = "sha256=" + HmacHex(MetaSecret, "original"),
        };
        Assert.False(NewVerifier().VerifyMetaSignature(headers, "tampered"));
    }

    [Fact]
    public void MetaSignature_MissingHeader_Fails()
    {
        Assert.False(NewVerifier().VerifyMetaSignature(new HeaderDictionary(), "body"));
    }

    [Fact]
    public void SkoolSignature_MatchingHmac_Verifies()
    {
        var body = "{\"plan\":\"inner_circle\"}";
        var headers = new HeaderDictionary { ["x-skool-signature"] = HmacHex(SkoolSecret, body) };
        Assert.True(NewVerifier().VerifySkoolSignature(headers, body));
    }

    [Fact]
    public void SkoolSignature_WrongSecret_Fails()
    {
        var body = "{\"plan\":\"inner_circle\"}";
        var headers = new HeaderDictionary { ["x-skool-signature"] = HmacHex("not-the-secret", body) };
        Assert.False(NewVerifier().VerifySkoolSignature(headers, body));
    }
}
