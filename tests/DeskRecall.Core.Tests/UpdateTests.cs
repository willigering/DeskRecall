using DeskRecall.Core.Settings;
using DeskRecall.Core.Update;
using Xunit;

namespace DeskRecall.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("v1.2.3-beta", 1, 2, 3)]
    [InlineData("1.1.0 (Build 2)", 1, 1, 0)]
    public void Parses_tags_and_display_versions(string tag, int a, int b, int c)
        => Assert.Equal(new Version(a, b, c), UpdateService.VersionFromTag(tag) ?? UpdateService.VersionFromAssembly(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("abc")]
    public void Rejects_invalid_tags(string? tag)
        => Assert.Null(UpdateService.VersionFromTag(tag));

    [Fact]
    public void Newer_ignores_revision_and_compares_major_minor_patch()
    {
        Assert.True(UpdateService.IsNewer(new Version(1, 1, 0), new Version(1, 0, 0)));
        Assert.True(UpdateService.IsNewer(new Version(1, 1, 0), new Version(1, 0, 0, 7)));
        Assert.False(UpdateService.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0, 99)));
        Assert.False(UpdateService.IsNewer(new Version(1, 0, 0), new Version(1, 1, 0)));
        Assert.False(UpdateService.IsNewer(null, new Version(1, 0, 0)));
        Assert.True(UpdateService.IsNewer(new Version(2, 0, 0), null));
        Assert.Equal("1.1.0", UpdateService.Display(new Version(1, 1, 0, 2)));
    }

    [Fact]
    public void Assembly_version_uses_the_text_before_the_build_suffix()
        => Assert.Equal(new Version(1, 1, 0), UpdateService.VersionFromAssembly("1.1.0 (Build 2)"));
}

public class UpdateAssetTests
{
    static readonly string[] CurrentRelease =
    {
        "DeskRecall-1.0.0-portable-win-x64.zip",
        "DeskRecall-1.0.0-win-x64.zip",
    };

    [Fact]
    public void Selects_stable_current_release_names()
    {
        Assert.Equal("DeskRecall-1.0.0-portable-win-x64.zip", UpdateService.SelectAsset(CurrentRelease, UpdateChannel.Portable));
        Assert.Equal("DeskRecall-1.0.0-win-x64.zip", UpdateService.SelectAsset(CurrentRelease, UpdateChannel.FrameworkDependent));
    }

    [Fact]
    public void Never_picks_split_parts_or_source_archives()
    {
        var names = new[]
        {
            "DeskRecall-1.1.0-source.zip",
            "DeskRecall-1.1.0-portable-win-x64-teil1von4.zip",
            "DeskRecall-1.1.0-portable-win-x64-teil2von4.zip",
            "DeskRecall-1.1.0-portable-win-x64.zip",
            "DeskRecall-1.1.0-win-x64.zip",
        };
        Assert.Equal("DeskRecall-1.1.0-portable-win-x64.zip", UpdateService.SelectAsset(names, UpdateChannel.Portable));
        Assert.Equal("DeskRecall-1.1.0-win-x64.zip", UpdateService.SelectAsset(names, UpdateChannel.FrameworkDependent));
    }

    [Fact]
    public void Framework_channel_does_not_fall_back_to_portable()
    {
        var onlyPortable = new[] { "DeskRecall-1.1.0-portable-win-x64.zip", "DeskRecall-1.1.0-source.zip" };
        Assert.Equal("DeskRecall-1.1.0-portable-win-x64.zip", UpdateService.SelectAsset(onlyPortable, UpdateChannel.Portable));
        Assert.Null(UpdateService.SelectAsset(onlyPortable, UpdateChannel.FrameworkDependent));
    }

    [Fact]
    public void Parses_github_latest_payload_and_binds_channel()
    {
        const string json = """
        {
          "tag_name": "v1.1.0",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/willigering/DeskRecall/releases/tag/v1.1.0",
          "body": "DeskRecall 1.1.0\n\nSHA-256 of DeskRecall-1.1.0-portable-win-x64.zip: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n",
          "assets": [
            { "name": "DeskRecall-1.1.0-portable-win-x64.zip", "browser_download_url": "https://example.test/p.zip", "size": 62000000 },
            { "name": "DeskRecall-1.1.0-portable-win-x64.zip.sha256", "browser_download_url": "https://example.test/p.sha", "size": 120 },
            { "name": "DeskRecall-1.1.0-win-x64.zip", "browser_download_url": "https://example.test/f.zip", "size": 425000 },
            { "name": "DeskRecall-1.1.0-win-x64.zip.sha256", "browser_download_url": "https://example.test/f.sha", "size": 120 }
          ]
        }
        """;
        var baseInfo = UpdateService.ParseRelease(json)!;
        Assert.Equal(new Version(1, 1, 0), baseInfo.Version);
        Assert.Contains("DeskRecall 1.1.0", baseInfo.Notes);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var assets = new List<(string, string, long)>();
        foreach (var x in doc.RootElement.GetProperty("assets").EnumerateArray())
            assets.Add((x.GetProperty("name").GetString()!, x.GetProperty("browser_download_url").GetString()!, x.GetProperty("size").GetInt64()));

        var portable = UpdateService.BindAsset(baseInfo, assets, UpdateChannel.Portable);
        Assert.Equal("DeskRecall-1.1.0-portable-win-x64.zip", portable.AssetName);
        Assert.Equal("https://example.test/p.zip", portable.AssetUrl);
        Assert.Equal(62000000, portable.AssetSize);
        Assert.Equal("https://example.test/p.sha", portable.Sha256AssetUrl);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", portable.Sha256);

        var fd = UpdateService.BindAsset(baseInfo, assets, UpdateChannel.FrameworkDependent);
        Assert.Equal("DeskRecall-1.1.0-win-x64.zip", fd.AssetName);
        Assert.Equal("https://example.test/f.zip", fd.AssetUrl);
        Assert.Null(fd.Sha256); // Hash im Text nennt nur das portable-Paket
        Assert.Equal("https://example.test/f.sha", fd.Sha256AssetUrl);
    }

    [Fact]
    public void Ignores_drafts_and_prereleases()
    {
        Assert.Null(UpdateService.ParseRelease("""{"tag_name":"v1.2.0","draft":true,"prerelease":false,"assets":[]}"""));
        Assert.Null(UpdateService.ParseRelease("""{"tag_name":"v1.2.0","draft":false,"prerelease":true,"assets":[]}"""));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  DeskRecall-1.1.0-win-x64.zip", "DeskRecall-1.1.0-win-x64.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("SHA-256: abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", null, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void Reads_sha256_from_text(string text, string? name, string expected)
        => Assert.Equal(expected, UpdateService.HashFromText(text, name));

    [Fact]
    public void Detects_portable_channel_from_flag()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dr-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(UpdateChannel.FrameworkDependent, UpdateService.DetectChannel(dir));
            File.WriteAllText(Path.Combine(dir, "portable.flag"), "x");
            Assert.Equal(UpdateChannel.Portable, UpdateService.DetectChannel(dir));
            Assert.Equal(UpdateChannel.Portable, UpdateService.DetectChannel(null, portableHint: true));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class UpdateFeedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_feed_uses_public_default(string? value)
        => Assert.Equal(UpdateService.DefaultFeedUrl, UpdateService.NormalizeFeedUrl(value));

    [Theory]
    [InlineData("https://github.com/willigering/DeskRecall")]
    [InlineData("https://github.com/willigering/DeskRecall/")]
    [InlineData("https://github.com/willigering/DeskRecall/releases")]
    [InlineData("https://github.com/willigering/DeskRecall/releases/latest")]
    [InlineData("https://api.github.com/repos/willigering/DeskRecall")]
    [InlineData("https://api.github.com/repos/willigering/DeskRecall/releases/latest")]
    public void Normalizes_github_repo_urls_to_latest_api(string url)
        => Assert.Equal("https://api.github.com/repos/willigering/DeskRecall/releases/latest", UpdateService.NormalizeFeedUrl(url));

    [Fact]
    public void Other_public_release_repos_are_rewritten()
        => Assert.Equal("https://api.github.com/repos/willigering/DeskRecall-Releases/releases/latest",
            UpdateService.NormalizeFeedUrl("https://github.com/willigering/DeskRecall-Releases"));

    [Fact]
    public void Custom_non_github_feed_is_kept()
        => Assert.Equal("https://example.test/feed.json", UpdateService.NormalizeFeedUrl("https://example.test/feed.json"));

    [Fact]
    public void Should_offer_respects_skip_except_for_manual_search()
    {
        var r = new ReleaseInfo(new Version(1, 1, 0), "v1.1.0", "", null, "a.zip", "https://x", 1, null, null);
        Assert.True(UpdateService.ShouldOffer(r, new Version(1, 0, 0), skipped: null, manual: false));
        Assert.False(UpdateService.ShouldOffer(r, new Version(1, 0, 0), skipped: "1.1.0", manual: false));
        Assert.True(UpdateService.ShouldOffer(r, new Version(1, 0, 0), skipped: "1.1.0", manual: true));
        Assert.False(UpdateService.ShouldOffer(r, new Version(1, 1, 0), skipped: null, manual: true));
    }

    [Fact]
    public void After_update_argument_is_read()
    {
        Assert.Equal("1.1.0", UpdateService.AfterUpdateVersion(new[] { "DeskRecall.exe", "--nach-update", "1.1.0" }));
        Assert.Equal("", UpdateService.AfterUpdateVersion(new[] { "--nach-update" }));
        Assert.Null(UpdateService.AfterUpdateVersion(new[] { "--other" }));
    }
}

public class WidgetAppearanceTests
{
    [Theory]
    [InlineData(260, WidgetWidthPreset.Narrow)]
    [InlineData(280, WidgetWidthPreset.Narrow)]
    [InlineData(290, WidgetWidthPreset.Normal)]
    [InlineData(300, WidgetWidthPreset.Normal)]
    [InlineData(310, WidgetWidthPreset.Wide)]
    [InlineData(400, WidgetWidthPreset.Wide)]
    public void Snaps_width_to_nearest_step(int px, WidgetWidthPreset expected)
        => Assert.Equal(expected, WidgetAppearance.FromPixels(px));

    [Fact]
    public void Opacity_changes_panel_and_tint_alpha_monotonically_and_keeps_a_readable_floor()
    {
        byte lastPanel = 0; uint lastTint = 0;
        for (var p = 30; p <= 100; p += 5)
        {
            var panel = WidgetAppearance.PanelAlpha(p, acrylic: false);
            var acrylicPanel = WidgetAppearance.PanelAlpha(p, acrylic: true);
            var tint = WidgetAppearance.AcrylicTintAbgr(p);
            Assert.True(panel >= lastPanel);
            Assert.True(tint >> 24 >= lastTint >> 24);
            Assert.True(acrylicPanel < panel); // Acryl-Fläche bleibt leichter
            Assert.True(panel >= 0x30); // nie unsichtbar
            lastPanel = panel; lastTint = tint;
        }
        Assert.True(WidgetAppearance.PanelAlpha(100, false) >= 250);
        Assert.NotEqual(
            WidgetAppearance.PanelAlpha(40, false),
            WidgetAppearance.PanelAlpha(90, false));
    }
}
