using System.Text.Json;
using ClearForge.Contracts;
using ClearForge.Engine.Apps;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using Xunit;

namespace ClearForge.Engine.Tests;

/// <summary>
/// M8.4a read-only application inventory: raw uninstall-hive interpretation
/// (classification, size, confidence), per-source failure isolation, and the
/// apps.list dispatch contract. No mutation surface exists in this milestone.
/// </summary>
public class M84Tests
{
    private static RawUninstallEntry Raw(
        string key = "AppKey",
        string? name = "Contoso App",
        string? publisher = "Contoso Ltd",
        string? version = "1.2.3",
        string? location = @"C:\Program Files\Contoso",
        long? sizeKb = 2048,
        string? installDate = "20240115",
        string? uninstall = @"C:\Program Files\Contoso\unins000.exe",
        bool systemComponent = false,
        string? parentKey = null) => new(
            key, name, publisher, version, location, sizeKb, installDate,
            uninstall, systemComponent, parentKey);

    private static UninstallHiveAppSource Source(
        string source, string scope, params RawUninstallEntry[] rows) =>
        new(() => rows, source, scope);

    // --------------------------- interpretation ---------------------------

    [Fact]
    public void Full_record_maps_all_fields()
    {
        var apps = Source("registry-hklm-64", "machine", Raw()).Enumerate();
        var a = Assert.Single(apps);
        Assert.Equal("Contoso App", a.Name);
        Assert.Equal("Contoso Ltd", a.Publisher);
        Assert.Equal("1.2.3", a.Version);
        Assert.Equal(@"C:\Program Files\Contoso", a.InstallLocation);
        Assert.Equal(2048L * 1024, a.SizeBytes);
        Assert.Equal("2024-01-15", a.InstallDate);
        Assert.Equal("registry-hklm-64", a.Source);
        Assert.Equal("machine", a.Scope);
        Assert.Equal("app", a.Kind);
        Assert.True(a.HasUninstaller);
        Assert.Equal("registered", a.Confidence);
    }

    [Fact]
    public void SystemComponent_classifies_as_system_component()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(systemComponent: true)).Enumerate());
        Assert.Equal("system-component", a.Kind);
    }

    [Fact]
    public void ParentKeyName_classifies_as_update()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(parentKey: "KB5034441")).Enumerate());
        Assert.Equal("update", a.Kind);
    }

    [Fact]
    public void Missing_uninstall_string_is_listed_only_not_registered()
    {
        var a = Assert.Single(Source("s", "current-user",
            Raw(uninstall: null)).Enumerate());
        Assert.False(a.HasUninstaller);
        Assert.Equal("listed-only", a.Confidence);
        Assert.Equal("app", a.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_display_name_is_skipped(string? name)
    {
        Assert.Empty(Source("s", "machine", Raw(name: name)).Enumerate());
    }

    [Fact]
    public void Missing_size_stays_null_not_zero()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(sizeKb: null)).Enumerate());
        Assert.Null(a.SizeBytes);
    }

    [Fact]
    public void Negative_size_is_not_reported_as_bytes()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(sizeKb: -4)).Enumerate());
        Assert.Null(a.SizeBytes);
    }

    [Fact]
    public void Non_iso_install_date_passes_through_raw()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(installDate: "15/01/2024")).Enumerate());
        Assert.Equal("15/01/2024", a.InstallDate);
    }

    [Fact]
    public void Malformed_fixed_length_install_date_passes_through_raw()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(installDate: "20241345")).Enumerate());
        Assert.Equal("20241345", a.InstallDate);
    }

    [Fact]
    public void Missing_optional_fields_stay_null()
    {
        var a = Assert.Single(Source("s", "machine",
            Raw(publisher: null, version: null, location: null,
                installDate: null)).Enumerate());
        Assert.Null(a.Publisher);
        Assert.Null(a.Version);
        Assert.Null(a.InstallLocation);
        Assert.Null(a.InstallDate);
    }

    // --------------------------- composition -------------------------------

    [Fact]
    public void Composite_survives_a_failing_source()
    {
        var composite = new CompositeAppInventory(new IAppInventorySource[]
        {
            Source("registry-hklm-64", "machine", Raw(key: "ok")),
            new ThrowingSource(),
            Source("registry-hkcu", "current-user",
                Raw(key: "user", name: "User App")),
        });
        var apps = composite.Enumerate();
        Assert.Equal(2, apps.Count);
        Assert.Contains(apps, a => a.Scope == "machine");
        Assert.Contains(apps, a => a.Scope == "current-user");
    }

    [Fact]
    public void Composite_sorts_by_name_case_insensitive()
    {
        var composite = new CompositeAppInventory(new IAppInventorySource[]
        {
            Source("s", "machine",
                Raw(key: "z", name: "zeta"),
                Raw(key: "A", name: "Alpha"),
                Raw(key: "m", name: "middle")),
        });
        Assert.Equal(new[] { "Alpha", "middle", "zeta" },
            composite.Enumerate().Select(a => a.Name).ToArray());
    }

    [Fact]
    public void Empty_enumeration_fails_safe_not_fake()
    {
        var composite = new CompositeAppInventory(new IAppInventorySource[]
        {
            Source("registry-hklm-64", "machine"),
            new ThrowingSource(),
        });
        Assert.Empty(composite.Enumerate());
    }

    // --------------------------- dispatch contract -------------------------

    private static RequestDispatcher Dispatcher(IAppInventorySource? apps)
    {
        var roots = new TrustedRoots();
        return new RequestDispatcher(
            new ScanSessionStore(Array.Empty<CleanupRule>(),
                new FileScanner(roots)),
            apps: apps);
    }

    private static async Task<JsonElement> Call(RequestDispatcher d, string method)
    {
        var res = await d.Dispatch(method, null);
        return JsonSerializer.SerializeToElement(res,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    [Fact]
    public async Task AppsList_returns_inventory_rows()
    {
        var d = Dispatcher(new CompositeAppInventory(new IAppInventorySource[]
        {
            Source("registry-hkcu", "current-user", Raw()),
        }));
        var res = await d.Dispatch("apps.list", null);
        Assert.Null(res.Error);
        var payload = JsonSerializer.SerializeToElement(res.Result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var apps = payload.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        var first = apps[0];
        Assert.Equal("Contoso App", first.GetProperty("name").GetString());
        Assert.Equal("current-user", first.GetProperty("scope").GetString());
        Assert.True(first.GetProperty("hasUninstaller").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            payload.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task AppsList_without_module_fails_closed()
    {
        var d = Dispatcher(null);
        var res = await d.Dispatch("apps.list", null);
        Assert.Equal("policy_refused", res.Error);
        Assert.Null(res.Result);
    }

    [Fact]
    public async Task AppsList_failure_in_one_hive_still_serves_others()
    {
        var d = Dispatcher(new CompositeAppInventory(new IAppInventorySource[]
        {
            new ThrowingSource(),
            Source("registry-hklm-64", "machine",
                Raw(key: "a", name: "Survivor")),
        }));
        var res = await d.Dispatch("apps.list", null);
        Assert.Null(res.Error);
        var payload = JsonSerializer.SerializeToElement(res.Result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal("Survivor",
            payload.GetProperty("apps")[0].GetProperty("name").GetString());
    }

    private sealed class ThrowingSource : IAppInventorySource
    {
        public IReadOnlyList<InstalledApp> Enumerate() =>
            throw new InvalidOperationException("hive unavailable");
    }
}
