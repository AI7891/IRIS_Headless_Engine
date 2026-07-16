using InnerShiftLab.ContentCreator;
using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace InnerShiftLab.Tests;

public class OutboxPackageBuilderTests : IDisposable
{
    private readonly string _outputRoot;
    private readonly FakeRepository _repo = new();
    private readonly FakeRenderer _renderer = new();
    private readonly IrisSettings _iris = new() { LinktreeUrl = "https://linktr.ee/test" };

    public OutboxPackageBuilderTests()
    {
        _outputRoot = Directory.CreateTempSubdirectory("iris_outbox_").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputRoot, true); } catch { /* best effort */ }
    }

    private OutboxPackageBuilder Builder(OutboxSettings? settings = null, IContentCreationPipeline? creator = null)
        => new(_renderer, _repo, settings ?? new OutboxSettings(), _iris, creator,
            _outputRoot, NullLogger<OutboxPackageBuilder>.Instance);

    private static PostSlot Slot() => new()
    {
        HookId = "hook-01",
        HookText = "Your skin is keeping the score your mind refuses to.",
        Pillar = Pillar.Identify,
        Caption = "🧠 Your skin is keeping the score your mind refuses to.\n\n" +
                  "https://linktr.ee/x?utm_source=iris&utm_medium=organic&utm_campaign=hook-01&utm_content=Identify\n\n" +
                  "#IdentifyYourSignal #TheInnerShiftLab #IRISMethod #VitiligoHealing #TraumaInformedHealing",
    };

    [Fact]
    public async Task Build_CreatesOneVariantPerPlatform_WithCorrectDimensions()
    {
        var package = await Builder().BuildAsync(Slot());

        Assert.Equal(4, package.Items.Count);
        foreach (var format in PlatformFormats.All)
        {
            var item = Assert.Single(package.Items, i => i.Platform == format.Platform);
            Assert.Equal(format.Width, item.Width);
            Assert.Equal(format.Height, item.Height);
            Assert.Equal(OutboxStatus.Pending, item.Status);
            Assert.True(File.Exists(item.MediaPath), $"media missing for {format.Platform}");
        }
        // Every variant also landed in the repository (SQLite = source of truth).
        Assert.Equal(4, _repo.Outbox.Count);
    }

    [Fact]
    public async Task Build_WritesCaptionFilesPreservingUtmLink()
    {
        var package = await Builder().BuildAsync(Slot());

        foreach (var item in package.Items)
        {
            var format = PlatformFormats.Get(item.Platform);
            // Titled platforms (YouTube) get description.txt; the rest caption.txt. The
            // tracking link (utm_campaign intact) is always inside that caption file.
            var captionFile = Path.Combine(package.PackageDir, item.Platform,
                format.TitleMaxChars > 0 ? "description.txt" : "caption.txt");
            Assert.True(File.Exists(captionFile), $"caption file missing for {item.Platform}");
            Assert.Contains("utm_campaign=hook-01", await File.ReadAllTextAsync(captionFile));
        }
    }

    [Fact]
    public async Task Build_Youtube_GetsDescriptionAlongsideTitle_NotCaption()
    {
        var package = await Builder().BuildAsync(Slot());

        var ytDir = Path.Combine(package.PackageDir, "youtube");
        Assert.True(File.Exists(Path.Combine(ytDir, "description.txt")));
        Assert.True(File.Exists(Path.Combine(ytDir, "title.txt")));
        Assert.False(File.Exists(Path.Combine(ytDir, "caption.txt")));

        var manifest = JObject.Parse(await File.ReadAllTextAsync(package.ManifestPath));
        var youtube = ((JArray)manifest["items"]!).Single(i => i.Value<string>("platform") == "youtube");
        Assert.Equal("youtube/description.txt", youtube.Value<string>("descriptionFile"));
        Assert.Equal("youtube/title.txt", youtube.Value<string>("titleFile"));
        Assert.Null(youtube.Value<string>("captionFile"));
    }

    [Fact]
    public async Task Build_WithContentCreator_UsesComposedMediaCaptionAndTitle()
    {
        var image = Path.Combine(_outputRoot, "slide1.png");
        var video = Path.Combine(_outputRoot, "composed.mp4");
        await File.WriteAllTextAsync(image, "ai-carousel-image");
        await File.WriteAllTextAsync(video, "ai-composed-video");
        var creator = new FakeCreator
        {
            Content = new ComposedContent
            {
                ScriptId = "sc1", Title = "AI Title", Caption = "AI caption body",
                SlideImagePaths = new[] { image }, VideoPath = video,
            },
        };
        var settings = new OutboxSettings { UseContentCreator = true };

        var package = await Builder(settings, creator).BuildAsync(Slot());

        // Image platforms get the resized carousel slide; video platforms the composed video.
        var ig = Assert.Single(package.Items, i => i.Platform == "instagram");
        Assert.Equal("ai-carousel-image", await File.ReadAllTextAsync(ig.MediaPath));
        var tiktok = Assert.Single(package.Items, i => i.Platform == "tiktok");
        Assert.EndsWith(".mp4", tiktok.MediaPath);
        Assert.Equal("ai-composed-video", await File.ReadAllTextAsync(tiktok.MediaPath));

        // AI caption is used (with the UTM link re-appended), YouTube title is the AI title.
        Assert.Contains("AI caption body", ig.Caption);
        Assert.Contains("utm_campaign=hook-01", ig.Caption);
        Assert.Equal("AI Title", Assert.Single(package.Items, i => i.Platform == "youtube").Title);
        Assert.Equal(0, _renderer.ImageCalls); // no text cards rendered
    }

    [Fact]
    public async Task Build_ContentCreatorThrows_FallsBackToTextCards()
    {
        var creator = new FakeCreator { Throw = true };
        var settings = new OutboxSettings { UseContentCreator = true, Platforms = new[] { "instagram" } };

        var package = await Builder(settings, creator).BuildAsync(Slot());

        // The pipeline failure is swallowed; the package still builds via text cards.
        var ig = Assert.Single(package.Items);
        Assert.True(File.Exists(ig.MediaPath));
        Assert.True(_renderer.ImageCalls > 0);
    }

    [Fact]
    public async Task Build_CreatorPresentButDisabled_RendersTextCards()
    {
        var creator = new FakeCreator { Content = new ComposedContent { ScriptId = "sc1" } };
        // UseContentCreator defaults to false.
        var package = await Builder(new OutboxSettings { Platforms = new[] { "instagram" } }, creator)
            .BuildAsync(Slot());

        Assert.False(creator.Called);
        Assert.True(_renderer.ImageCalls > 0);
        Assert.NotEmpty(package.Items);
    }

    [Fact]
    public async Task Build_Instagram_CaptionHasBioCueAndRawUrl_FacebookHasNeither()
    {
        var package = await Builder().BuildAsync(Slot());

        var igCaption = await File.ReadAllTextAsync(Path.Combine(package.PackageDir, "instagram", "caption.txt"));
        Assert.Contains(PlatformFormatter.LinkInBioCta, igCaption);
        Assert.Contains("utm_source=instagram", igCaption);
        Assert.Contains("https://linktr.ee/", igCaption);

        var fbCaption = await File.ReadAllTextAsync(Path.Combine(package.PackageDir, "facebook", "caption.txt"));
        Assert.DoesNotContain(PlatformFormatter.LinkInBioCta, fbCaption);
        Assert.Contains("https://linktr.ee/", fbCaption);
    }

    [Fact]
    public async Task Build_NonClickablePlatforms_GetBareLinkFile_ClickableDoNot()
    {
        var package = await Builder().BuildAsync(Slot());

        // Instagram + TikTok: a link.txt with the bare tracked URL and nothing else.
        foreach (var platform in new[] { "instagram", "tiktok" })
        {
            var linkPath = Path.Combine(package.PackageDir, platform, "link.txt");
            Assert.True(File.Exists(linkPath), $"link.txt missing for {platform}");
            var link = await File.ReadAllTextAsync(linkPath);
            Assert.StartsWith("https://linktr.ee/", link);
            Assert.Contains($"utm_source={platform}", link);
            Assert.DoesNotContain(PlatformFormatter.LinkInBioCta, link);
            Assert.DoesNotContain("\n", link);
            // The caption still carries the cue + URL — link.txt is an extra convenience.
            Assert.Contains(PlatformFormatter.LinkInBioCta,
                await File.ReadAllTextAsync(Path.Combine(package.PackageDir, platform, "caption.txt")));
        }

        // Clickable platforms: no link.txt (the URL is clickable in the caption).
        Assert.False(File.Exists(Path.Combine(package.PackageDir, "facebook", "link.txt")));
        Assert.False(File.Exists(Path.Combine(package.PackageDir, "youtube", "link.txt")));

        // Manifest reflects it: linkFile set for IG, null for Facebook.
        var manifest = JObject.Parse(await File.ReadAllTextAsync(package.ManifestPath));
        var items = (JArray)manifest["items"]!;
        Assert.Equal("instagram/link.txt", items.Single(i => i.Value<string>("platform") == "instagram").Value<string>("linkFile"));
        Assert.Null(items.Single(i => i.Value<string>("platform") == "facebook").Value<string>("linkFile"));
    }

    [Fact]
    public async Task Build_WritesTitleFileOnlyForYoutube()
    {
        var package = await Builder().BuildAsync(Slot());

        Assert.True(File.Exists(Path.Combine(package.PackageDir, "youtube", "title.txt")));
        Assert.False(File.Exists(Path.Combine(package.PackageDir, "instagram", "title.txt")));
    }

    [Fact]
    public async Task Build_WritesParseableManifest()
    {
        var package = await Builder().BuildAsync(Slot());

        Assert.True(File.Exists(package.ManifestPath));
        var manifest = JObject.Parse(await File.ReadAllTextAsync(package.ManifestPath));
        Assert.Equal(package.PackageId, manifest.Value<string>("packageId"));
        Assert.Equal("hook-01", manifest.Value<string>("hookId"));
        var items = (JArray)manifest["items"]!;
        Assert.Equal(4, items.Count);
        var youtube = items.Single(i => i.Value<string>("platform") == "youtube");
        Assert.Equal("1080x1920", youtube.Value<string>("dimensions"));
        Assert.Equal($"/api/outbox/{package.PackageId}/youtube/confirm", youtube.Value<string>("confirmEndpoint"));
    }

    [Fact]
    public async Task Build_VideoPlatformsGetVideo_ImagePlatformsGetImage()
    {
        var package = await Builder().BuildAsync(Slot());

        Assert.EndsWith(".mp4", Assert.Single(package.Items, i => i.Platform == "tiktok").MediaPath);
        Assert.EndsWith(".mp4", Assert.Single(package.Items, i => i.Platform == "youtube").MediaPath);
        Assert.EndsWith(".png", Assert.Single(package.Items, i => i.Platform == "instagram").MediaPath);
    }

    [Fact]
    public async Task Build_VideoRenderFailure_FallsBackToImage()
    {
        _renderer.FailVideo = true;
        var package = await Builder().BuildAsync(Slot());

        Assert.EndsWith(".png", Assert.Single(package.Items, i => i.Platform == "tiktok").MediaPath);
        Assert.Equal(4, package.Items.Count); // failure did not sink the package
    }

    [Fact]
    public async Task Build_RenderVideoDisabled_NeverInvokesVideoRenderer()
    {
        var package = await Builder(new OutboxSettings { RenderVideo = false }).BuildAsync(Slot());

        Assert.Equal(0, _renderer.VideoCalls);
        Assert.All(package.Items, i => Assert.EndsWith(".png", i.MediaPath));
    }

    [Fact]
    public async Task Build_RestrictsToRequestedPlatforms()
    {
        var package = await Builder().BuildAsync(Slot(), new[] { "instagram", "youtube" });

        Assert.Equal(2, package.Items.Count);
        Assert.Contains(package.Items, i => i.Platform == "instagram");
        Assert.Contains(package.Items, i => i.Platform == "youtube");
        Assert.False(Directory.Exists(Path.Combine(package.PackageDir, "tiktok")));
    }

    [Fact]
    public void EffectivePlatforms_EmptyList_FallsBackToAllSupported()
    {
        Assert.Equal(OutboxSettings.DefaultPlatforms, new OutboxSettings().EffectivePlatforms);
    }

    [Fact]
    public void EffectivePlatforms_DeduplicatesBinderAppendedValues()
    {
        // The configuration binder appends bound array elements to existing ones;
        // EffectivePlatforms must neutralize any resulting duplication.
        var settings = new OutboxSettings
        {
            Platforms = new[] { "instagram", "tiktok", "Instagram", "tiktok" },
        };
        Assert.Equal(new[] { "instagram", "tiktok" }, settings.EffectivePlatforms);
    }

    [Fact]
    public async Task LocalExporter_ReturnsPackageDir()
    {
        var package = await Builder().BuildAsync(Slot());
        var exporter = new LocalPackageExporter(NullLogger<LocalPackageExporter>.Instance);

        Assert.Equal(package.PackageDir, await exporter.ExportAsync(package));
    }

    private sealed class FakeRenderer : IContentRenderer
    {
        public bool FailVideo;
        public int VideoCalls;
        public int ImageCalls;

        public Task<string> RenderImageAsync(string text, string? outPath = null, string palette = "iris-default",
            int width = 1080, int height = 1080)
        {
            ImageCalls++;
            outPath ??= Path.GetTempFileName();
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, $"img {width}x{height}");
            return Task.FromResult(outPath);
        }

        public Task<string> RenderPdfAsync(string title, IEnumerable<string> sections, string? outPath = null)
            => throw new NotSupportedException();

        public Task<string> RenderVideoAsync(string text, string backgroundPath, string? outPath = null, int durationSec = 15)
        {
            VideoCalls++;
            if (FailVideo) throw new InvalidOperationException("ffmpeg unavailable");
            outPath ??= Path.GetTempFileName();
            File.WriteAllText(outPath, "video");
            return Task.FromResult(outPath);
        }

        // Resize helpers stub as a byte-preserving copy so tests can assert on content.
        public Task<string> ResizeImageAsync(string sourcePath, string outPath, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.Copy(sourcePath, outPath, overwrite: true);
            return Task.FromResult(outPath);
        }

        public Task<string> ResizeVideoAsync(string sourcePath, string outPath, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.Copy(sourcePath, outPath, overwrite: true);
            return Task.FromResult(outPath);
        }
    }

    private sealed class FakeCreator : IContentCreationPipeline
    {
        public ComposedContent Content = new();
        public bool Throw;
        public bool Called;

        public Task<ComposedContent> CreateAsync(string? keywords = null, int? slideCount = null, CancellationToken ct = default)
        {
            Called = true;
            if (Throw) throw new InvalidOperationException("anthropic down");
            return Task.FromResult(Content);
        }

        public Task<IReadOnlyList<PostSlot>> CreateAndPublishAsync(string? keywords, string[] platforms, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
