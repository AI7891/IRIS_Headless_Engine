using InnerShiftLab.Auth;
using InnerShiftLab.Core;
using Xunit;

namespace InnerShiftLab.Tests;

public class TokenVaultTests
{
    private static (TokenVault Vault, string Dir) NewVault()
    {
        var dir = Directory.CreateTempSubdirectory("iris_vault_").FullName;
        // Drop a marker so AppPaths.ResolveRoot anchors to this temp dir (hermetic).
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
        return (new TokenVault(new TestEnv(dir)), dir);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var (vault, _) = NewVault();
        var tokens = new TokenSet
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            PageAccessToken = "page-789",
            PageId = "pid",
            IgBusinessId = "ig-1",
            IgUsername = "@lab",
            ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await vault.SaveTokensAsync("meta", tokens);
        var loaded = await vault.LoadTokensAsync("meta");

        Assert.NotNull(loaded);
        Assert.Equal("access-123", loaded!.AccessToken);
        Assert.Equal("refresh-456", loaded.RefreshToken);
        Assert.Equal("page-789", loaded.PageAccessToken);
        Assert.Equal("ig-1", loaded.IgBusinessId);
        Assert.Equal(tokens.ExpiresAt, loaded.ExpiresAt);
    }

    [Fact]
    public async Task LoadMissingProvider_ReturnsNull()
    {
        var (vault, _) = NewVault();
        Assert.Null(await vault.LoadTokensAsync("nonexistent"));
    }

    [Fact]
    public async Task StoredFile_IsEncryptedAtRest()
    {
        var (vault, dir) = NewVault();
        await vault.SaveTokensAsync("tiktok", new TokenSet { AccessToken = "super-secret-token" });

        var tokFile = Path.Combine(dir, "data", "tiktok.tok");
        Assert.True(File.Exists(tokFile));
        var raw = await File.ReadAllTextAsync(tokFile);
        Assert.DoesNotContain("super-secret-token", raw);
    }
}
