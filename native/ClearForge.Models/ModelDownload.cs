using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClearForge.Models;

/// <summary>Stable, UI-safe failure — the wire code is carried in Code.</summary>
public sealed class ModelException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One raw HTTP response; redirects are never followed here.</summary>
public sealed class TransportResponse : IDisposable
{
    public required int StatusCode { get; init; }
    public Uri? RedirectLocation { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentRange { get; init; }
    public string? ETag { get; init; }
    public Stream? Body { get; init; }

    public void Dispose() => Body?.Dispose();
}

/// <summary>
/// The download boundary: one GET, optionally ranged, returning status +
/// headers + body stream. All policy (scheme, hosts, size) is enforced by
/// the caller so a substituted transport cannot weaken it.
/// </summary>
public interface IModelTransport
{
    Task<TransportResponse> SendAsync(Uri uri, long rangeStart, CancellationToken ct);
}

/// <summary>
/// HttpClient-backed transport. Auto-redirect is disabled — every hop is
/// re-validated by the download policy. HTTPS only; the
/// <paramref name="permitPlainHttp"/> switch exists solely for loopback
/// test fixtures and must never be set in production wiring.
/// </summary>
public sealed class HttpModelTransport : IModelTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _permitPlainHttp;

    public HttpModelTransport(bool permitPlainHttp = false)
    {
        _permitPlainHttp = permitPlainHttp;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<TransportResponse> SendAsync(
        Uri uri, long rangeStart, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !_permitPlainHttp)
            throw new ModelException("policy_refused",
                $"refusing non-https endpoint '{uri.Scheme}'");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (rangeStart > 0)
            req.Headers.Range = new RangeHeaderValue(rangeStart, null);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelException("download_failed", $"transport error: {ex.Message}");
        }
        return new TransportResponse
        {
            StatusCode = (int)resp.StatusCode,
            RedirectLocation = resp.Headers.Location,
            ContentLength = resp.Content.Headers.ContentLength,
            ContentRange = resp.Content.Headers.ContentRange?.ToString(),
            ETag = resp.Headers.ETag?.Tag,
            Body = resp.IsSuccessStatusCode || (int)resp.StatusCode is >= 300 and < 400
                ? await resp.Content.ReadAsStreamAsync(ct)
                : null,
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Redirect-following, size-enforcing artifact fetch. Policy enforced here:
/// every redirect target must be HTTPS and within the catalogue-pinned host
/// allowlist; Content-Length must agree with the pinned size; a resume is
/// only honoured when the server returns a well-formed 206 for the exact
/// requested offset and total.
/// </summary>
internal static class ArtifactFetcher
{
    private const int MaxRedirects = 5;
    private const int BufferBytes = 256 * 1024;

    public static bool IsRedirect(int status) =>
        status is 301 or 302 or 303 or 307 or 308;

    /// <summary>
    /// Stream the artifact to <paramref name="partPath"/>. An existing part
    /// file is resumed when the server validates the range; otherwise the
    /// file is truncated and the download restarts. On return the file is
    /// exactly entry.SizeBytes long (hash verification is the caller's job).
    /// Throws ModelException (failed) or ModelInterruptedException
    /// (resumable stop: cancel / disconnect / truncation).
    /// </summary>
    public static async Task FetchAsync(
        ModelCatalogEntry entry,
        IModelTransport transport,
        string partPath,
        Func<string, FileMode, Stream> openWrite,
        Action<long> onProgress,
        CancellationToken ct)
    {
        var expected = entry.Artifact.SizeBytes;
        long have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (have > expected)
        {
            // A part file larger than the pinned size can only be corrupt —
            // never resume it.
            File.Delete(partPath);
            have = 0;
        }
        if (have == expected)
        {
            onProgress(have);
            return; // download already complete; caller verifies hash
        }

        Uri uri = new(entry.Artifact.Url);
        for (var hop = 0; ; hop++)
        {
            using var resp = await transport.SendAsync(uri, have, ct);
            if (IsRedirect(resp.StatusCode))
            {
                if (hop >= MaxRedirects)
                    throw new ModelException("download_failed", "redirect limit exceeded");
                if (resp.RedirectLocation is null)
                    throw new ModelException("download_failed", "redirect without location");
                var next = resp.RedirectLocation.IsAbsoluteUri
                    ? resp.RedirectLocation
                    : new Uri(uri, resp.RedirectLocation);
                if (next.Scheme != Uri.UriSchemeHttps)
                    throw new ModelException("policy_refused",
                        "redirect to non-https refused");
                if (!ModelCatalog.IsHostAllowed(next.Host, entry.Artifact.AllowedRedirectHosts))
                    throw new ModelException("policy_refused",
                        $"redirect to unapproved host '{next.Host}' refused");
                uri = next;
                continue;
            }

            if (resp.StatusCode == 206 && have > 0)
            {
                var (start, total) = ParseContentRange(resp.ContentRange);
                if (start != have || total != expected)
                    throw new ModelException("download_failed",
                        "server returned an invalid resume range — restarting is required");
            }
            else if (resp.StatusCode == 200)
            {
                if (have > 0)
                {
                    // Range ignored — safe resume impossible, restart cleanly.
                    have = 0;
                    File.Delete(partPath);
                }
                if (resp.ContentLength is { } len && len != expected)
                    throw new ModelException("download_failed",
                        $"unexpected content length {len}, expected {expected}");
            }
            else
            {
                throw new ModelException("download_failed",
                    $"unexpected HTTP status {resp.StatusCode}");
            }

            if (resp.Body is null)
                throw new ModelException("download_failed", "empty response body");
            if (have > 0 && resp.ContentLength is { } rlen && rlen != expected - have)
                throw new ModelException("download_failed",
                    "resume length does not match the pinned artifact size");

            await CopyBoundedAsync(resp.Body, partPath, have, expected,
                entry.Artifact.Format, openWrite, onProgress, ct);
            return;
        }
    }

    private static async Task CopyBoundedAsync(
        Stream body, string partPath, long offset, long expected,
        string format, Func<string, FileMode, Stream> openWrite,
        Action<long> onProgress, CancellationToken ct)
    {
        var mode = offset > 0 ? FileMode.Append : FileMode.Create;
        await using var file = openWrite(partPath, mode);
        var buf = new byte[BufferBytes];
        var firstWrite = offset == 0;
        long written = offset;
        while (true)
        {
            int n;
            try { n = await body.ReadAsync(buf, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ModelInterruptedException($"connection dropped: {ex.Message}");
            }
            if (n == 0) break;

            if (firstWrite)
            {
                if (format == "gguf"
                    && (n < 4 || buf[0] != 'G' || buf[1] != 'G' || buf[2] != 'U' || buf[3] != 'F'))
                    throw new ModelException("download_failed",
                        "artifact does not start with the GGUF magic — unexpected content");
                firstWrite = false;
            }

            if (written + n > expected)
                throw new ModelException("download_failed",
                    "response exceeds the pinned artifact size");

            try { await file.WriteAsync(buf.AsMemory(0, n), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ModelInterruptedException($"disk write failed: {ex.Message}");
            }
            written += n;
            onProgress(written);
        }
        await file.FlushAsync(ct);
        if (written != expected)
            throw new ModelInterruptedException(
                $"response ended at {written} of {expected} bytes");
    }

    /// <summary>"bytes start-end/total" for 206 responses.</summary>
    internal static (long Start, long Total) ParseContentRange(string? header)
    {
        // Format: bytes <start>-<end>/<total>
        if (header is null || !header.StartsWith("bytes ", StringComparison.Ordinal))
            return (-1, -1);
        var range = header[6..].Split('/', 2);
        if (range.Length != 2 || !long.TryParse(range[1], out var total))
            return (-1, -1);
        var ends = range[0].Split('-', 2);
        if (ends.Length != 2 || !long.TryParse(ends[0], out var start)
            || !long.TryParse(ends[1], out var end) || end < start)
            return (-1, -1);
        return (start, total);
    }
}

/// <summary>Resumable stoppage: cancellation, dropped connection,
/// truncation or disk-write failure. The part file is kept for resume.</summary>
public sealed class ModelInterruptedException(string message) : Exception(message);
