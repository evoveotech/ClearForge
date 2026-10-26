using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClearForge.Models;

public sealed class ModelCatalogException(string message) : Exception(message);

/// <summary>One downloadable artifact with pinned provenance.</summary>
public sealed record ModelArtifact(
    string FileName,
    string Url,
    string Sha256,
    long SizeBytes,
    string Format,
    IReadOnlyList<string> AllowedRedirectHosts);

public sealed record ModelLicense(string Id, string Url);

public sealed record ModelRuntime(
    string Backend, string Acceleration, IReadOnlyList<string> Architectures);

/// <summary>RAM/storage guidance — estimates, never guarantees.</summary>
public sealed record ModelRequirements(
    long MinRamBytesEstimate, long MinFreeStorageBytesEstimate);

/// <summary>
/// One curated, application-approved model. Every field required by the
/// schema is validated at load; the catalogue can describe a model but can
/// never carry executable content or post-install actions — unknown fields
/// are rejected outright.
/// </summary>
public sealed record ModelCatalogEntry(
    string Id,
    string DisplayName,
    string Publisher,
    string Repository,
    string? UpstreamRepository,
    string Revision,
    ModelArtifact Artifact,
    ModelLicense License,
    ModelRuntime Runtime,
    ModelRequirements Requirements,
    string SupportStatus,
    string QualityNotes)
{
    public const string Recommended = "recommended";
    public const string Experimental = "experimental";
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Versioned, application-owned catalogue of explicitly approved models.
/// The bundled document is embedded in this assembly; parsing is strict:
/// unknown fields, malformed identifiers, non-HTTPS sources, unverifiable
/// digests or mutable revisions all fail closed.
/// </summary>
public sealed class ModelCatalog
{
    public const int SupportedVersion = 1;

    private static readonly Regex IdPattern =
        new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex FileNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern =
        new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex RevisionPattern =
        new("^[0-9a-f]{40}$", RegexOptions.Compiled);

    public int Version { get; }
    public IReadOnlyList<ModelCatalogEntry> Models { get; }

    private ModelCatalog(int version, IReadOnlyList<ModelCatalogEntry> models)
    {
        Version = version;
        Models = models;
    }

    public ModelCatalogEntry? Find(string id) =>
        Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The catalogue compiled into this assembly — the only
    /// trusted source for this phase.</summary>
    public static ModelCatalog LoadBundled()
    {
        var asm = typeof(ModelCatalog).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("catalog.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(s);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Strict parse + full validation; throws ModelCatalogException.</summary>
    public static ModelCatalog Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ModelCatalogException($"catalogue is not valid JSON: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ModelCatalogException("catalogue root must be an object");
            RequireOnly(root, "catalogue", "catalogVersion", "models");

            var version = RequireInt(root, "catalogue", "catalogVersion");
            if (version != SupportedVersion)
                throw new ModelCatalogException($"unsupported catalogVersion {version}");

            if (!root.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ModelCatalogException("catalogue.models must be an array");

            var models = new List<ModelCatalogEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in arr.EnumerateArray())
            {
                var m = ParseEntry(el);
                if (!seen.Add(m.Id))
                    throw new ModelCatalogException($"duplicate model id '{m.Id}'");
                models.Add(m);
            }
            if (models.Count == 0)
                throw new ModelCatalogException("catalogue contains no models");
            return new ModelCatalog(version, models);
        }
    }

    private static ModelCatalogEntry ParseEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new ModelCatalogException("model entry must be an object");
        RequireOnly(e, "model", "id", "displayName", "publisher", "repository",
            "upstreamRepository", "revision", "artifact", "license", "runtime",
            "requirements", "supportStatus", "qualityNotes", "evaluation");

        var id = RequireString(e, "model", "id");
        if (!IdPattern.IsMatch(id))
            throw new ModelCatalogException($"model id '{id}' is not a safe identifier");

        var revision = RequireString(e, "model", "revision");
        if (!RevisionPattern.IsMatch(revision))
            throw new ModelCatalogException(
                $"model '{id}': revision must be an immutable 40-hex commit");

        var status = RequireString(e, "model", "supportStatus");
        if (status is not (ModelCatalogEntry.Recommended
            or ModelCatalogEntry.Experimental or ModelCatalogEntry.Unsupported))
            throw new ModelCatalogException($"model '{id}': unsupported status '{status}'");

        return new ModelCatalogEntry(
            id,
            RequireString(e, "model", "displayName"),
            RequireString(e, "model", "publisher"),
            RequireString(e, "model", "repository"),
            OptionalString(e, "upstreamRepository"),
            revision,
            ParseArtifact(e, id),
            ParseLicense(e, id),
            ParseRuntime(e, id),
            ParseRequirements(e, id),
            status,
            RequireString(e, "model", "qualityNotes"));
    }

    private static ModelArtifact ParseArtifact(JsonElement e, string id)
    {
        var a = RequireObject(e, "model", "artifact");
        RequireOnly(a, $"model '{id}' artifact", "fileName", "url", "sha256",
            "sizeBytes", "format", "allowedRedirectHosts");

        var fileName = RequireString(a, "artifact", "fileName");
        if (!FileNamePattern.IsMatch(fileName) || fileName.Contains("..")
            || !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ModelCatalogException(
                $"model '{id}': artifact fileName '{fileName}' is not a safe .gguf name");

        var format = RequireString(a, "artifact", "format");
        if (!string.Equals(format, "gguf", StringComparison.Ordinal))
            throw new ModelCatalogException($"model '{id}': unsupported format '{format}'");

        var sha = RequireString(a, "artifact", "sha256");
        if (!Sha256Pattern.IsMatch(sha))
            throw new ModelCatalogException(
                $"model '{id}': sha256 must be 64 lowercase hex characters");

        var size = RequireLong(a, "artifact", "sizeBytes");
        if (size <= 0 || size > (1L << 40))
            throw new ModelCatalogException($"model '{id}': sizeBytes out of range");

        if (!a.TryGetProperty("allowedRedirectHosts", out var hostsEl)
            || hostsEl.ValueKind != JsonValueKind.Array)
            throw new ModelCatalogException(
                $"model '{id}': allowedRedirectHosts must be an array");
        var hosts = new List<string>();
        foreach (var h in hostsEl.EnumerateArray())
        {
            var host = h.GetString();
            if (host is null || !IsHostPattern(host))
                throw new ModelCatalogException(
                    $"model '{id}': invalid redirect host pattern");
            hosts.Add(host);
        }
        if (hosts.Count == 0)
            throw new ModelCatalogException(
                $"model '{id}': allowedRedirectHosts must not be empty");

        var url = RequireString(a, "artifact", "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new ModelCatalogException(
                $"model '{id}': artifact url must be absolute https");
        if (!IsHostAllowed(uri.Host, hosts))
            throw new ModelCatalogException(
                $"model '{id}': artifact url host is not in allowedRedirectHosts");

        return new ModelArtifact(fileName, url, sha, size, format, hosts);
    }

    private static ModelLicense ParseLicense(JsonElement e, string id)
    {
        var l = RequireObject(e, "model", "license");
        RequireOnly(l, $"model '{id}' license", "id", "url");
        var lid = RequireString(l, "license", "id");
        var url = RequireString(l, "license", "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new ModelCatalogException($"model '{id}': license url must be https");
        return new ModelLicense(lid, url);
    }

    private static ModelRuntime ParseRuntime(JsonElement e, string id)
    {
        var r = RequireObject(e, "model", "runtime");
        RequireOnly(r, $"model '{id}' runtime", "backend", "acceleration", "architectures");
        if (!r.TryGetProperty("architectures", out var arch)
            || arch.ValueKind != JsonValueKind.Array)
            throw new ModelCatalogException($"model '{id}': runtime.architectures required");
        var archs = arch.EnumerateArray()
            .Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0).ToArray();
        if (archs.Length == 0 || !archs.Contains("x64", StringComparer.Ordinal))
            throw new ModelCatalogException(
                $"model '{id}': runtime must list the x64 architecture");
        return new ModelRuntime(
            RequireString(r, "runtime", "backend"),
            RequireString(r, "runtime", "acceleration"),
            archs);
    }

    private static ModelRequirements ParseRequirements(JsonElement e, string id)
    {
        var r = RequireObject(e, "model", "requirements");
        RequireOnly(r, $"model '{id}' requirements",
            "minRamBytesEstimate", "minFreeStorageBytesEstimate", "estimatesAreApproximate");
        var ram = RequireLong(r, "requirements", "minRamBytesEstimate");
        var storage = RequireLong(r, "requirements", "minFreeStorageBytesEstimate");
        if (ram <= 0 || storage <= 0)
            throw new ModelCatalogException(
                $"model '{id}': requirement estimates must be positive");
        return new ModelRequirements(ram, storage);
    }

    /// <summary>
    /// Host allowlist check: an entry is either an exact host or a
    /// "*.suffix" wildcard covering subdomains only — never bare "*" and
    /// never a suffix match that would also accept "evil-suffix".
    /// </summary>
    public static bool IsHostAllowed(string host, IReadOnlyList<string> allowed)
    {
        foreach (var a in allowed)
        {
            if (a.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = a[2..];
                if (host.Length > suffix.Length
                    && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && host[^ (suffix.Length + 1)] == '.')
                    return true;
            }
            else if (string.Equals(host, a, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsHostPattern(string pattern)
    {
        if (pattern.Length == 0 || pattern.Length > 253
            || pattern != pattern.Trim().ToLowerInvariant()
            || pattern.Any(char.IsWhiteSpace))
            return false;
        if (!pattern.Contains('*'))
            return true;
        // Wildcards only as a "*.suffix" leading segment — subdomains, never
        // the bare suffix and never a mid-string star.
        return pattern.StartsWith("*.", StringComparison.Ordinal)
            && pattern.IndexOf('*') == 0 && pattern.Length > 2;
    }

    private static void RequireOnly(JsonElement obj, string what, params string[] fields)
    {
        var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            if (!allowed.Contains(p.Name))
                throw new ModelCatalogException($"{what}: unsupported field '{p.Name}'");
    }

    private static JsonElement RequireObject(JsonElement obj, string what, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ModelCatalogException($"{what}.{name} must be an object");
        return el;
    }

    private static string RequireString(JsonElement obj, string what, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ModelCatalogException($"{what}.{name} must be a string");
        var s = el.GetString()!;
        if (s.Length == 0 || s.Length > 2048)
            throw new ModelCatalogException($"{what}.{name} is empty or too long");
        return s;
    }

    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private static int RequireInt(JsonElement obj, string what, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt32(out var v))
            throw new ModelCatalogException($"{what}.{name} must be an integer");
        return v;
    }

    private static long RequireLong(JsonElement obj, string what, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt64(out var v))
            throw new ModelCatalogException($"{what}.{name} must be an integer");
        return v;
    }
}
