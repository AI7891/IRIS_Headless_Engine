using InnerShiftLab.Outbox;
using Xunit;

namespace InnerShiftLab.Tests;

public class PlatformFormatterTests
{
    // Matches the neutral pre-format default emitted by IrisEngine.BuildCaption.
    private const string UtmLink =
        "https://linktr.ee/dennis.p.santillan87?utm_source=iris&utm_medium=organic&utm_campaign=hook-01&utm_content=Integrate&utm_term=iris";

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
            // The stamped link lives in the caption for clickable platforms, and in
            // Variant.Link (shipped as link.txt) for link-in-bio platforms.
            var link = f.LinkInBio ? v.Link : v.Caption;
            Assert.Contains("https://linktr.ee/dennis.p.santillan87?", link);
            Assert.Contains("utm_campaign=hook-01", link);
            Assert.Contains("utm_content=Integrate", link);
            Assert.Contains("utm_term=iris", link);
        }
    }

    [Fact]
    public void Format_StampsUtmSourceWithPlatform()
    {
        foreach (var f in PlatformFormats.All)
        {
            var v = PlatformFormatter.Format(f.Platform, "hook", EngineCaption("hook"));
            // The engine emits a neutral utm_source=iris; each variant must carry its
            // own platform so a Skool join can be attributed per platform.
            var link = f.LinkInBio ? v.Link : v.Caption;
            Assert.Contains($"utm_source={f.Platform}", link);
            Assert.DoesNotContain("utm_source=iris", link);
        }
    }

    [Fact]
    public void Format_RewritesUtmMediumToManual()
    {
        foreach (var f in PlatformFormats.All)
        {
            var v = PlatformFormatter.Format(f.Platform, "hook", EngineCaption("hook"));
            var link = f.LinkInBio ? v.Link : v.Caption;
            // Posting is manual now — utm_medium=organic is rewritten accordingly.
            Assert.Contains("utm_medium=manual", link);
            Assert.DoesNotContain("utm_medium=organic", link);
        }
    }

    [Fact]
    public void Format_CampaignAndContent_AreByteIdenticalToInput()
    {
        foreach (var f in PlatformFormats.All)
        {
            var v = PlatformFormatter.Format(f.Platform, "hook", EngineCaption("hook"));
            var link = f.LinkInBio ? v.Link : v.Caption;
            // utm_campaign / utm_content carry hook + pillar and must never be touched.
            Assert.Contains("utm_campaign=hook-01", link);
            Assert.Contains("utm_content=Integrate", link);
        }
    }

    [Fact]
    public void Format_CaptionWithNoLink_IsUnaffected()
    {
        var caption = "just a body line\n\n#Tag #Two";
        var v = PlatformFormatter.Format("facebook", "body", caption);
        Assert.DoesNotContain("utm_", v.Caption);
        Assert.Equal("", v.Link);
    }

    [Fact]
    public void Format_Instagram_ReplacesDeadUrlWithBioCta()
    {
        var v = PlatformFormatter.Format("instagram", "hook", EngineCaption("hook"));

        // IG captions are not clickable: no raw URL in the caption, a bio CTA instead,
        // and the stamped link available separately for the operator's bio/Linktree.
        Assert.DoesNotContain("https://", v.Caption);
        Assert.Contains(PlatformFormatter.LinkInBioCta, v.Caption);
        Assert.Contains("utm_source=instagram", v.Link);
    }

    [Fact]
    public void Format_Instagram_NoLinkInCaption_HasNoCtaAndEmptyLink()
    {
        var v = PlatformFormatter.Format("instagram", "hook", "just a body\n\n#Tag");
        Assert.DoesNotContain(PlatformFormatter.LinkInBioCta, v.Caption);
        Assert.Equal("", v.Link);
    }

    [Fact]
    public void Format_ClickablePlatforms_KeepLinkInCaption()
    {
        foreach (var f in PlatformFormats.All.Where(f => !f.LinkInBio))
        {
            var v = PlatformFormatter.Format(f.Platform, "hook", EngineCaption("hook"));
            Assert.Contains("https://linktr.ee/", v.Caption);
            Assert.DoesNotContain(PlatformFormatter.LinkInBioCta, v.Caption);
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
        var v = PlatformFormatter.Format("facebook", "body", caption);
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
