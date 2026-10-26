using ClearForge.Engine.Startup;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.5 startup completeness: StartupApproved\Run blob decoding, disabled-state
/// join on Run/RunOnce entries, and source-label propagation for the expanded
/// hive coverage. All read-only — no toggle/write surface exists.
/// </summary>
public class M85Tests
{
    // ------------------------- blob decoding -------------------------------

    [Theory]
    [InlineData(0x02, "enabled")]
    [InlineData(0x03, "enabled")] // enabled + measured impact
    [InlineData(0x06, "disabled")]
    [InlineData(0x07, "unknown")] // undocumented state — never guessed
    [InlineData(0x00, "unknown")]
    [InlineData(0xFF, "unknown")]
    public void StartupApproved_state_byte_decodes(byte state, string expected)
    {
        Assert.Equal(expected, StartupApprovedState.Decode(new byte[] { state, 0, 0 }));
    }

    [Fact]
    public void No_approval_record_means_enabled()
    {
        Assert.Equal("enabled", StartupApprovedState.Decode(null));
        Assert.Equal("enabled", StartupApprovedState.Decode(Array.Empty<byte>()));
    }

    // ------------------------- source entries ------------------------------

    [Fact]
    public void Disabled_entry_reports_disabled_not_silently_dropped()
    {
        var approvals = new Dictionary<string, byte[]?>
        {
            ["DisabledApp"] = new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        };
        var src = new RunKeyStartupSource(
            () => new[] { ("DisabledApp", @"C:\Apps\a.exe") },
            _ => "Vendor",
            "registry-hklm64-run",
            name => approvals.TryGetValue(name, out var b) ? b : null);
        var e = Assert.Single(src.Enumerate());
        Assert.Equal("disabled", e.Status);
        Assert.Equal("registry-hklm64-run", e.Source);
    }

    [Fact]
    public void Entry_without_approval_blob_stays_enabled()
    {
        var src = new RunKeyStartupSource(
            () => new[] { ("PlainApp", @"C:\Apps\b.exe") },
            _ => null,
            "registry-hkcu-run",
            _ => null);
        var e = Assert.Single(src.Enumerate());
        Assert.Equal("enabled", e.Status);
        Assert.Equal("registry-hkcu-run", e.Source);
    }

    [Fact]
    public void Unrecognized_blob_reports_unknown_not_enabled()
    {
        var src = new RunKeyStartupSource(
            () => new[] { ("OddApp", @"C:\Apps\c.exe") },
            _ => null,
            "registry-hklm32-run",
            _ => new byte[] { 0x09 });
        var e = Assert.Single(src.Enumerate());
        Assert.Equal("unknown", e.Status);
    }

    [Fact]
    public void No_approval_lookup_preserves_legacy_enabled_behavior()
    {
        var src = new RunKeyStartupSource(
            () => new[] { ("LegacyApp", @"C:\Apps\d.exe") }, _ => null);
        Assert.Equal("enabled", Assert.Single(src.Enumerate()).Status);
    }

    [Fact]
    public void Machine_and_user_sources_are_distinct_labels()
    {
        var user = new RunKeyStartupSource(
            () => new[] { ("App", @"C:\a.exe") }, _ => null, "registry-hkcu-run");
        var machine = new RunKeyStartupSource(
            () => new[] { ("App", @"C:\a.exe") }, _ => null, "registry-hklm64-run");
        Assert.Equal("registry-hkcu-run",
            Assert.Single(user.Enumerate()).Source);
        Assert.Equal("registry-hklm64-run",
            Assert.Single(machine.Enumerate()).Source);
    }
}
