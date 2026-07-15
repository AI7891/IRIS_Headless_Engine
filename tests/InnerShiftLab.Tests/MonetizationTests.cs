using InnerShiftLab.Core;
using InnerShiftLab.Monetization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Xunit;

namespace InnerShiftLab.Tests;

public class MonetizationTests
{
    private static (MonetizationLogger Logger, FakeRepository Repo) NewLogger()
    {
        var repo = new FakeRepository();
        var settings = Options.Create(new MonetizationSettings
        {
            InnerCirclePriceEur = 37m,
            RootWorkLabPriceEur = 197m,
            StarterKitPriceEur = 27m,
        });
        return (new MonetizationLogger(repo, settings, NullLogger<MonetizationLogger>.Instance), repo);
    }

    [Theory]
    [InlineData("inner_circle", 37)]
    [InlineData("root_work_lab", 197)]
    [InlineData("starter_kit", 27)]
    public async Task SkoolJoin_MapsPlanToRevenue(string plan, decimal expected)
    {
        var (logger, repo) = NewLogger();
        var body = JsonConvert.SerializeObject(new { plan, @ref = "utm_campaign=hookA&utm_content=Stabilise" });

        await logger.LogSkoolJoinAsync(body);

        var c = Assert.Single(repo.Conversions);
        Assert.Equal(expected, c.RevenueEur);
        Assert.Equal("skool_join", c.EventType);
        Assert.Equal("hookA", c.UtmCampaign);
        Assert.Equal("Stabilise", c.UtmContent);
    }

    [Fact]
    public async Task SkoolJoin_UnknownPlan_HasNoRevenue()
    {
        var (logger, repo) = NewLogger();
        var body = JsonConvert.SerializeObject(new { plan = "mystery", @ref = "" });

        await logger.LogSkoolJoinAsync(body);

        var c = Assert.Single(repo.Conversions);
        Assert.Null(c.RevenueEur);
    }

    [Fact]
    public async Task SkoolJoin_MalformedBody_DoesNotThrowOrRecord()
    {
        var (logger, repo) = NewLogger();
        await logger.LogSkoolJoinAsync("this is not json {");
        Assert.Empty(repo.Conversions);
    }

    [Fact]
    public async Task SkoolJoin_UsesUtmSourceAsPlatform_WhenPayloadHasNone()
    {
        var (logger, repo) = NewLogger();
        var body = JsonConvert.SerializeObject(new
        {
            plan = "inner_circle",
            @ref = "utm_source=tiktok&utm_campaign=hookA&utm_content=Identify",
        });

        await logger.LogSkoolJoinAsync(body);

        Assert.Equal("tiktok", Assert.Single(repo.Conversions).Platform);
    }

    [Fact]
    public async Task SkoolJoin_ExplicitPayloadPlatform_WinsOverUtmSource()
    {
        var (logger, repo) = NewLogger();
        var body = JsonConvert.SerializeObject(new
        {
            plan = "inner_circle",
            platform = "instagram",
            @ref = "utm_source=tiktok&utm_campaign=hookA",
        });

        await logger.LogSkoolJoinAsync(body);

        Assert.Equal("instagram", Assert.Single(repo.Conversions).Platform);
    }

    [Fact]
    public async Task SkoolJoin_NoPlatformAnywhere_FallsBackToSkool()
    {
        var (logger, repo) = NewLogger();
        var body = JsonConvert.SerializeObject(new { plan = "inner_circle", @ref = "utm_campaign=hookA" });

        await logger.LogSkoolJoinAsync(body);

        Assert.Equal("skool", Assert.Single(repo.Conversions).Platform);
    }

    [Fact]
    public async Task Summary_AggregatesByPlatform()
    {
        var (logger, _) = NewLogger();
        await logger.LogClickAsync("p1", "instagram", "hookA", "Identify");
        await logger.LogSkoolJoinAsync(JsonConvert.SerializeObject(
            new { plan = "inner_circle", @ref = "utm_source=instagram&utm_campaign=hookA" }));
        await logger.LogSkoolJoinAsync(JsonConvert.SerializeObject(
            new { plan = "root_work_lab", @ref = "utm_source=tiktok&utm_campaign=hookA" }));

        var summary = await logger.GetSummaryAsync();

        // Highest-revenue platform first — this answers "which platform converts?".
        Assert.Equal("tiktok", summary.ByPlatform.First().Platform);
        Assert.Equal(197m, summary.ByPlatform.First().RevenueEur);
        var ig = summary.ByPlatform.Single(p => p.Platform == "instagram");
        Assert.Equal(1, ig.Joins);
        Assert.Equal(1, ig.Clicks);
        Assert.Equal(37m, ig.RevenueEur);
    }

    [Fact]
    public async Task Summary_AggregatesRevenueAndCountsByCampaign()
    {
        var (logger, repo) = NewLogger();
        await logger.LogClickAsync("p1", "instagram", "hookA", "Identify");
        await logger.LogSkoolJoinAsync(JsonConvert.SerializeObject(
            new { plan = "inner_circle", @ref = "utm_campaign=hookA&utm_content=Identify" }));
        await logger.LogSkoolJoinAsync(JsonConvert.SerializeObject(
            new { plan = "root_work_lab", @ref = "utm_campaign=hookB&utm_content=Stabilise" }));

        var summary = await logger.GetSummaryAsync();

        Assert.Equal(2, summary.TotalJoins);
        Assert.Equal(1, summary.TotalClicks);
        Assert.Equal(234m, summary.TotalRevenueEur); // 37 + 197
        // Highest-revenue campaign first.
        Assert.Equal("hookB", summary.ByCampaign.First().Campaign);
        Assert.Equal(197m, summary.ByCampaign.First().RevenueEur);
        var hookA = summary.ByCampaign.Single(x => x.Campaign == "hookA");
        Assert.Equal(1, hookA.Joins);
        Assert.Equal(1, hookA.Clicks);
    }
}
