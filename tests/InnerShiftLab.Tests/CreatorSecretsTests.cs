using InnerShiftLab.ContentCreator;
using Xunit;

namespace InnerShiftLab.Tests;

public class CreatorSecretsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_ME")]
    [InlineData("REPLACE_ME_OR_SET_ANTHROPIC_API_KEY_ENV")]
    [InlineData("replace_me")]
    public void IsConfigured_RejectsPlaceholdersAndEmpty(string? value)
    {
        Assert.False(CreatorSecrets.IsConfigured(value));
    }

    [Theory]
    [InlineData("sk-ant-api03-abc123")]
    [InlineData("some-real-key")]
    public void IsConfigured_AcceptsRealValues(string value)
    {
        Assert.True(CreatorSecrets.IsConfigured(value));
    }

    [Fact]
    public void Resolve_PrefersConfiguredValueOverEnv()
    {
        Environment.SetEnvironmentVariable("CREATOR_SECRETS_TEST", "from-env");
        try
        {
            Assert.Equal("from-config", CreatorSecrets.Resolve("from-config", "CREATOR_SECRETS_TEST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CREATOR_SECRETS_TEST", null);
        }
    }

    [Fact]
    public void Resolve_FallsBackToEnvWhenPlaceholderShipped()
    {
        Environment.SetEnvironmentVariable("CREATOR_SECRETS_TEST", "from-env");
        try
        {
            // The appsettings.json shipped with this placeholder — it must not shadow the env var.
            Assert.Equal("from-env", CreatorSecrets.Resolve("REPLACE_ME_OR_SET_ANTHROPIC_API_KEY_ENV", "CREATOR_SECRETS_TEST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CREATOR_SECRETS_TEST", null);
        }
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNothingConfigured()
    {
        Environment.SetEnvironmentVariable("CREATOR_SECRETS_TEST", null);
        Assert.Null(CreatorSecrets.Resolve("REPLACE_ME", "CREATOR_SECRETS_TEST"));
        Assert.Null(CreatorSecrets.Resolve(null, envFallback: null));
    }
}
