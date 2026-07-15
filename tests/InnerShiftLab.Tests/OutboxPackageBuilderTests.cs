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

    public OutboxPackageBuilderTests()
    {
        _outputRoot = Directory.CreateTempSubdirectory("iris_outbox_").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputRoot, true); } catch { /* best effort */ }
    }

    private OutboxPackageBuilder Builder(OutboxSettings? settings = null)
        => new(_renderer, _repo, settings ?? new OutboxSettings(),
            _outputRoot, NullLogger<OutboxPackageBuilder>.Instance);

    private static PostSlot Slot() => new()
    {
        HookId = "hook-01",
        HookText = "Your skin is keeping the score your mind refuses to.",
        Pillar = Pillar.Identify,
        Caption = "🧠 Your skin is keeping the score your mind refuses to.\n\n" +
                  "https://linktr.ee/x?utm_source=auto&utm_campaign=hook-01&utm_content=Identify\n\n" +
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
            var captionFile = Path.Combine(package.PackageDir, item.Platform, "caption.txt");
            Assert.True(File.Exists(captionFile));
            // Link-in-bio platforms carry the tracking link in link.txt instead of the caption.
            var format = PlatformFormats.Get(item.Platform);
            var attributedFile = format.LinkInBio
                ? Path.Combine(package.PackageDir, item.Platform, "link.txt")
                : captionFile;
            Assert.Contains("utm_campaign=hook-01", await File.ReadAllTextAsync(attributedFile));
        }
    }

    [Fact]
    public async Task Build_InstagramGetsLinkFileAndBioCta_OthersDoNot()
    {
        var package = await Builder().BuildAsync(Slot());

        var igDir = Path.Combine(package.PackageDir, "instagram");
        Assert.True(File.Exists(Path.Combine(igDir, "link.txt")));
        Assert.Contains("utm_source=instagram", await File.ReadAllTextAsync(Path.Combine(igDir, "link.txt")));
        Assert.Contains(PlatformFormatter.LinkInBioCta,
            await File.ReadAllTextAsync(Path.Combine(igDir, "caption.txt")));
        Assert.False(File.Exists(Path.Combine(package.PackageDir, "facebook", "link.txt")));
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

        public Task<string> RenderImageAsync(string text, string? outPath = null, string palette = "iris-default",
            int width = 1080, int height = 1080)
        {
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
    }
}
