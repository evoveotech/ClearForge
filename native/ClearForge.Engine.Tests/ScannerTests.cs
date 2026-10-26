using ClearForge.Engine.Classification;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>Disposable fixture directory per test. Never touches real temp roots.</summary>
public sealed class FixtureDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "cf-test", Guid.NewGuid().ToString("N"));

    public FixtureDir() => Directory.CreateDirectory(Path);

    public string Write(string name, string content = "x", DateTimeOffset? lastWriteUtc = null)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, (lastWriteUtc ?? DateTimeOffset.UtcNow.AddHours(-48)).UtcDateTime);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

public class ScannerTests
{
    private static CleanupRule Rule(string rootId = "test-root", long minAgeHours = 24, long max = 50_000) =>
        new("r1", 1, "temporary-files", "current-user", rootId, minAgeHours,
            "low", true, "quarantine", max, true, false);

    private static (FileScanner, List<ScannedFile>) Scan(FixtureDir fx, CleanupRule rule, CancellationToken ct = default)
    {
        var roots = new TrustedRoots().Add("test-root", fx.Path);
        var scanner = new FileScanner(roots);
        var files = scanner.Enumerate(rule, "s", i => $"c{i}", ct).ToList();
        return (scanner, files);
    }

    [Fact]
    public void Enumerates_files_and_respects_subdirectories()
    {
        using var fx = new FixtureDir();
        fx.Write("a.tmp"); fx.Write("b.tmp"); fx.Write("sub/c.tmp");
        var (_, files) = Scan(fx, Rule());
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.True(TrustedRoots.IsContainedIn(
            TrustedRoots.Canonicalize(fx.Path), f.CanonicalPath)));
    }

    [Fact]
    public void Scan_is_read_only()
    {
        using var fx = new FixtureDir();
        var p = fx.Write("keep.tmp", "payload");
        var before = new FileInfo(p);
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p));
        var mtime = before.LastWriteTimeUtc;

        Scan(fx, Rule());

        var after = new FileInfo(p);
        Assert.Equal(mtime, after.LastWriteTimeUtc);
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
    }

    [Fact]
    public void Junction_outside_root_is_not_traversed()
    {
        using var fx = new FixtureDir();
        using var outside = new FixtureDir();
        outside.Write("secret.txt");
        fx.Write("inside.tmp");

        // NTFS junctions do not require admin, unlike symlinks.
        var link = Path.Combine(fx.Path, "escape-link");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{link}\" \"{outside.Path}\"")
        { CreateNoWindow = true, UseShellExecute = false };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
        Assert.True(Directory.Exists(link));
        Assert.Equal(FileAttributes.ReparsePoint,
            new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint);

        var (_, files) = Scan(fx, Rule());
        Assert.Single(files);
        Assert.DoesNotContain(files, f => f.CanonicalPath.Contains("escape-link", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.CanonicalPath.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_limit_is_enforced()
    {
        using var fx = new FixtureDir();
        for (var i = 0; i < 10; i++) fx.Write($"f{i}.tmp");
        var (_, files) = Scan(fx, Rule(max: 3));
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void Cancellation_stops_scan()
    {
        using var fx = new FixtureDir();
        for (var i = 0; i < 50; i++) fx.Write($"f{i}.tmp");
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var roots = new TrustedRoots().Add("test-root", fx.Path);
        var scanner = new FileScanner(roots);
        Assert.Throws<OperationCanceledException>(() =>
            scanner.Enumerate(Rule(), "s", i => $"c{i}", cts.Token).ToList());
    }

    [Fact]
    public void Unknown_root_yields_nothing()
    {
        using var fx = new FixtureDir();
        var roots = new TrustedRoots(); // root never registered
        var scanner = new FileScanner(roots);
        Assert.Empty(scanner.Enumerate(Rule("ghost-root"), "s", i => $"c{i}", default));
    }

    [Fact]
    public void Classifier_drops_files_younger_than_threshold()
    {
        using var fx = new FixtureDir();
        fx.Write("old.tmp", lastWriteUtc: DateTimeOffset.UtcNow.AddDays(-3));
        fx.Write("fresh.tmp", lastWriteUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        var roots = new TrustedRoots().Add("test-root", fx.Path);
        var rule = Rule();
        var files = new FileScanner(roots).Enumerate(rule, "s", i => $"c{i}", default).ToList();
        Assert.Equal(2, files.Count); // scanner sees both

        var classified = files.Select(f => Classifier.TryClassify(f, rule, DateTimeOffset.UtcNow))
                              .Where(c => c is not null).ToList();
        Assert.Single(classified); // classifier admits only the old file
        Assert.Contains("old.tmp", classified[0]!.DisplayName);
    }

    [Fact]
    public void Classifier_never_overstates_risk_for_unknown_rule_risk()
    {
        var weird = Rule() with { Risk = "mystery" };
        var file = new ScannedFile("c0", "x", weird.Id, weird.Category, 1,
            DateTimeOffset.UtcNow.AddDays(-2), FileAttributes.Normal);
        var c = Classifier.TryClassify(file, weird, DateTimeOffset.UtcNow);
        Assert.Equal(Contracts.RiskLevel.Restricted, c!.Risk);
    }
}
