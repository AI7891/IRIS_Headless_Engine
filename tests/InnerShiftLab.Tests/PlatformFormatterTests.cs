using InnerShiftLab.Outbox;
using Xunit;

namespace InnerShiftLab.Tests;

public class PlatformFormatterTests
{
    private const string UtmLink =
        "https://linktr.ee/dennis.p.santillan87?utm_source=auto&utm_medium=social&utm_campaign=hook-01&utm_content=Integrate&utm_term=iris";

    private static string EngineCaption(string hookText) =>
$@"🧠 {hookText}

{UtmLink}

#IntegrateTheRejected #TheInnerShiftLab #IRISMethod #VitiligoHealing #TraumaInformedHealing #HolisticHealing";

    [Theory]
    [InlineData("instagram", 1080, 1350)]
    [InlineData("facebook",  1080, 1350)]
    [InlineData("tiktok",    1080, 1920)]
    [InlineData("youtube",   1080, 1920)]
    public void Formats_HaveExpectedDimensions(string platform, int width, int height)
    {
        var f = PlatformFormats.Get(platform);
        Assert.Equal(width, f.Width);
        Assert.Equal(height, f.Height);
    }

    [Fact]
    public void Get_UnknownPlatform_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlatformFormats.Get("myspace"));
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        Assert.Equal("instagram", PlatformFormats.Get("Instagram").Platform);
    }

    [Fact]
    public void Format_Facebook_CapsHashtagsAtThree()
    {
        var v = PlatformFormatter.Format("facebook", "hook", EngineCaption("hook"));
        var tags = v.Caption.Split(' ', '\n').Count(t => t.StartsWith('#'));
        Assert.Equal(3, tags);
        // The first hashtag (the pillar tag) is the one that must survive.
        Assert.Contains("#IntegrateTheRejected", v.Caption);
    }

    [Fact]
    public void Format_KeepsUtmCampaignAndContentIntact()
    {
        foreach (var f in PlatformFormats.All)
        {
            var v = PlatformFormatter.Format(f.Platform, "hook", EngineCaption("hook"));
            Assert.Contains("https://linktr.ee/dennis.p.santillan87?", v.Caption);
            Assert.Contains("utm_campaign=hook-01", v.Caption);
            Assert.Contains("utm_content=Integrate", v.Caption);
            Assert.Contains("utm_term=iris", v.Caption);
        }
    }

    [Fact]
    public void Format_StampsUtmSourceWithPlatform()
    {
        foreach (var f in PlatformFormats.All)
        {
            var v = PlatformFormatter.Format(f.Platform, "hook", EngineCaption("hook"));
            // The engine emits utm_source=auto; each variant must carry its own platform
            // so a Skool join can be attributed to Instagram vs TikTok vs YouTube.
            Assert.Contains($"utm_source={f.Platform}", v.Caption);
            Assert.DoesNotContain("utm_source=auto", v.Caption);
        }
    }

    [Fact]
    public void Format_LinkWithoutUtmSource_GetsItAppended()
    {
        var caption = "body\n\nhttps://linktr.ee/x?utm_campaign=hook-01\n\n#Tag";
        var v = PlatformFormatter.Format("tiktok", "body", caption);
        Assert.Contains("utm_campaign=hook-01&utm_source=tiktok", v.Caption);
    }

    [Fact]
    public void Format_NonTrackingLink_IsLeftUntouched()
    {
        var caption = "body\n\nhttps://example.com/article\n\n#Tag";
        var v = PlatformFormatter.Format("instagram", "body", caption);
        Assert.Contains("https://example.com/article", v.Caption);
        Assert.DoesNotContain("example.com/article?", v.Caption);
    }

    [Fact]
    public void Format_OverlongBody_TrimsBodyButPreservesLinkAndHashtags()
    {
        var longHook = string.Join(" ", Enumerable.Repeat("nervous system regulation is trainable", 200));
        var v = PlatformFormatter.Format("tiktok", longHook, EngineCaption(longHook));

        Assert.True(v.Caption.Length <= 2200, $"caption was {v.Caption.Length} chars");
        Assert.Contains("utm_campaign=hook-01", v.Caption);
        Assert.Contains("utm_source=tiktok", v.Caption);
        Assert.Contains("#IntegrateTheRejected", v.Caption);
        Assert.Contains("…", v.Caption);
    }

    [Fact]
    public void Format_ShortCaption_IsNotTrimmed()
    {
        var v = PlatformFormatter.Format("instagram", "Short hook.", EngineCaption("Short hook."));
        Assert.Contains("Short hook.", v.Caption);
        Assert.DoesNotContain("…", v.Caption);
    }

    [Fact]
    public void Format_Youtube_DerivesTitleWithinLimit()
    {
        var longHook = string.Join(" ", Enumerable.Repeat("word", 60));
        var v = PlatformFormatter.Format("youtube", longHook, EngineCaption(longHook));
        Assert.NotEqual("", v.Title);
        Assert.True(v.Title.Length <= 100, $"title was {v.Title.Length} chars");
    }

    [Fact]
    public void Format_NonTitlePlatforms_HaveEmptyTitle()
    {
        var v = PlatformFormatter.Format("instagram", "hook", EngineCaption("hook"));
        Assert.Equal("", v.Title);
    }

    [Fact]
    public void Format_DeduplicatesHashtags()
    {
        var caption = "body text\n\n#Same #same #Other";
        var v = PlatformFormatter.Format("instagram", "body text", caption);
        var tags = v.Caption.Split(' ', '\n').Where(t => t.StartsWith('#')).ToList();
        Assert.Equal(2, tags.Count);
    }
}
