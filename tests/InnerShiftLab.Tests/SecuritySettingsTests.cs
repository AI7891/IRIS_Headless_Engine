using InnerShiftLab.Security;
using Xunit;

namespace InnerShiftLab.Tests;

public class SecuritySettingsTests
{
    private static SecuritySettings With(string key, bool require = true)
        => new() { RequireApiKey = require, ApiKey = key };

    [Fact]
    public void Validate_Accepts32CharKey()
    {
        SecuritySettings.Validate(With(new string('k', 32))); // no throw
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-key")]                 // < 32
    [InlineData("change-me")]
    [InlineData("change-me-in-env")]
    [InlineData("secret")]
    [InlineData("test")]
    [InlineData("CHANGE-ME")]                 // placeholder check is case-insensitive
    public void Validate_ThrowsOnMissingShortOrPlaceholder(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SecuritySettings.Validate(With(key)));
        // The message tells the operator exactly how to fix it…
        Assert.Contains("IRIS_API_KEY", ex.Message);
        Assert.Contains("openssl rand", ex.Message);
        // …and never echoes the value.
        if (!string.IsNullOrWhiteSpace(key))
            Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SkippedWhenApiKeyNotRequired()
    {
        SecuritySettings.Validate(With("", require: false)); // local dev: no throw
    }
}
