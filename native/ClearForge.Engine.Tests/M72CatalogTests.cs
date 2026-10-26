using ClearForge.Ai;
using ClearForge.Models;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M7.2 catalogue trust: the bundled catalogue is the only source of
/// installable models, and its strict schema fails closed on anything it
/// cannot verify — including fields that could smuggle executable intent.
/// </summary>
public class M72CatalogTests
{
    private static string EntryJson(
        string id = "test-model",
        string url = "https://fixture.local/repo/test.gguf",
        string? sha = null,
        long size = 64,
        string fileName = "test.gguf",
        string revision = "0123456789abcdef0123456789abcdef01234567",
        string status = "recommended",
        string hosts = "\"fixture.local\"",
        string format = "gguf",
        string? extraEntryField = null) => $$"""
        {
          "id": "{{id}}",
          "displayName": "Test Model",
          "publisher": "test",
          "repository": "test/repo",
          "revision": "{{revision}}",
          "artifact": {
            "fileName": "{{fileName}}",
            "url": "{{url}}",
            "sha256": "{{sha ?? new string('a', 64)}}",
            "sizeBytes": {{size}},
            "format": "{{format}}",
            "allowedRedirectHosts": [{{hosts}}]
          },
          "license": { "id": "apache-2.0", "url": "https://www.apache.org/licenses/LICENSE-2.0" },
          "runtime": { "backend": "test", "acceleration": "cpu", "architectures": ["x64"] },
          "requirements": { "minRamBytesEstimate": 1024, "minFreeStorageBytesEstimate": 2048, "estimatesAreApproximate": true },
          "supportStatus": "{{status}}",
          "qualityNotes": "fixture",
          "evaluation": null{{(extraEntryField is null ? "" : ", " + extraEntryField)}}
        }
        """;

    private static string CatalogJson(params string[] entries) =>
        $"{{\"catalogVersion\":1,\"models\":[{string.Join(',', entries)}]}}";

    private static ModelCatalogEntry Parse1(string entry)
        => ModelCatalog.Parse(CatalogJson(entry)).Models.Single();

    [Fact]
    public void Bundled_catalogue_loads_and_pins_the_qwen_artifact()
    {
        var c = ModelCatalog.LoadBundled();
        Assert.Equal(1, c.Version);
        var m = Assert.Single(c.Models);
        Assert.Equal("qwen2.5-1.5b-instruct-q4_k_m", m.Id);
        // The catalogue pins exactly what the runtime independently enforces.
        Assert.Equal(CuratedModel.ExpectedSha256, m.Artifact.Sha256);
        Assert.Equal(CuratedModel.ExpectedSize, m.Artifact.SizeBytes);
        Assert.Equal(CuratedModel.FileName, m.Artifact.FileName);
        Assert.Equal(CuratedModel.HfRevision, m.Revision);
        Assert.Equal(ModelCatalogEntry.Recommended, m.SupportStatus);
        Assert.StartsWith("https://huggingface.co/", m.Artifact.Url);
    }

    [Fact]
    public void Catalogue_unknown_id_is_not_found()
    {
        var c = ModelCatalog.LoadBundled();
        Assert.Null(c.Find("not-a-model"));
        Assert.Null(c.Find("../qwen2.5-1.5b-instruct-q4_k_m"));
    }

    [Fact]
    public void Duplicate_ids_rejected()
        => Assert.Throws<ModelCatalogException>(() =>
            ModelCatalog.Parse(CatalogJson(EntryJson(), EntryJson())));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"catalogVersion\":99,\"models\":[]}")]
    [InlineData("{\"catalogVersion\":1,\"models\":[]}")]
    [InlineData("{\"catalogVersion\":1}")]
    public void Malformed_documents_rejected(string json)
        => Assert.Throws<ModelCatalogException>(() => ModelCatalog.Parse(json));

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("UPPER")]
    [InlineData("-lead")]
    [InlineData("")]
    public void Invalid_model_ids_rejected(string id)
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(id: id)));

    [Theory]
    [InlineData("http://fixture.local/x.gguf")]           // non-https
    [InlineData("ftp://fixture.local/x.gguf")]            // non-http scheme
    [InlineData("https://evil.example/x.gguf")]           // host not allowlisted
    [InlineData("not-a-url")]
    public void Invalid_or_untrusted_urls_rejected(string url)
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(url: url)));

    [Fact]
    public void Redirect_to_allowed_subdomain_pattern_validates()
    {
        var e = Parse1(EntryJson(hosts: "\"fixture.local\",\"*.cdn.fixture.local\""));
        Assert.True(ModelCatalog.IsHostAllowed(
            "a.cdn.fixture.local", e.Artifact.AllowedRedirectHosts));
        Assert.False(ModelCatalog.IsHostAllowed(
            "cdn.fixture.local", e.Artifact.AllowedRedirectHosts));   // bare suffix
        Assert.False(ModelCatalog.IsHostAllowed(
            "evil-cdn.fixture.local.evil.com", e.Artifact.AllowedRedirectHosts));
        Assert.False(ModelCatalog.IsHostAllowed(
            "fixture.local.evil.com", e.Artifact.AllowedRedirectHosts));
    }

    [Theory]
    [InlineData("abcdef")]                                  // too short
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abc")] // 63
    [InlineData("1adf0b11065d8ad2e8123ea110d1ec956dab4ab038eab665614adba04b6c337g")] // non-hex
    public void Invalid_sha256_rejected(string sha)
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(sha: sha)));

    [Fact]
    public void Uppercase_sha_rejected()
        => Assert.Throws<ModelCatalogException>(() =>
            Parse1(EntryJson(sha: new string('A', 64))));

    [Theory]
    [InlineData("..\\evil.gguf")]
    [InlineData("sub/dir.gguf")]
    [InlineData("evil.exe")]
    [InlineData(".gguf")]
    [InlineData("a..gguf")]
    public void Invalid_filenames_rejected(string name)
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(fileName: name)));

    [Theory]
    [InlineData("main")]                                    // branch, not immutable
    [InlineData("9eadc66")]                                 // short sha
    [InlineData("9eadc66189c7641e1ddd226b8267a9119b2ce2d")] // 39 hex
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public void Mutable_or_invalid_revisions_rejected(string rev)
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(revision: rev)));

    [Theory]
    [InlineData("blessed")]
    [InlineData("")]
    public void Unknown_support_status_rejected(string status)
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(status: status)));

    [Fact]
    public void Non_gguf_format_rejected()
        => Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(format: "zip")));

    [Fact]
    public void Catalogue_cannot_carry_executable_content()
    {
        // A field that could encode a post-install command is an unknown
        // field — the strict schema refuses the document outright.
        Assert.Throws<ModelCatalogException>(() =>
            Parse1(EntryJson(extraEntryField:
                "\"postInstall\": \"powershell -c Remove-Item C:\\\\\"")));
        Assert.Throws<ModelCatalogException>(() =>
            Parse1(EntryJson(extraEntryField: "\"command\": \"x.exe\"")));
    }

    [Fact]
    public void Zero_and_negative_sizes_rejected()
    {
        Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(size: 0)));
        Assert.Throws<ModelCatalogException>(() => Parse1(EntryJson(size: -5)));
    }
}
