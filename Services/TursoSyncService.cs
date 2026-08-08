using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PosApp.Services;

/// <summary>
/// Status of a completed (or attempted) backup, raised via <see cref="TursoSyncService.StatusChanged"/>.
/// </summary>
public record SyncStatus(bool Success, bool Enabled, DateTime Time, string Message);

/// <summary>
/// Backs up the local SQLite database to a remote Turso (libSQL) database on a
/// schedule. The local DB remains the source of truth; this pushes a full
/// snapshot to the server over the libSQL HTTP "Hrana over HTTP" v2 pipeline API,
/// so no native libSQL dependency is required.
///
/// Configuration (never hard-coded):
///   - URL:   env TURSO_DATABASE_URL, else the built-in default. libsql:// is
///            rewritten to https://.
///   - Token: env TURSO_AUTH_TOKEN, else a "turso_auth_token.txt" file placed
///            next to the executable. If no token is found, backup is disabled
///            (the app still runs fully on the local database).
/// </summary>
public class TursoSyncService
{
    private const string DefaultUrl = "libsql://metal-pos-lxw99.aws-ap-northeast-1.turso.io";
    private const int InsertChunkSize = 64;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly DatabaseService _db;
    private readonly string _httpBase;
    private readonly string? _authToken;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private TimeSpan _interval = TimeSpan.FromHours(1);

    public TursoSyncService(DatabaseService db)
    {
        _db = db;

        var url = EnvConfig.Get("TURSO_URL", "TURSO_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(url))
            url = DefaultUrl;
        _httpBase = ToHttpBase(url);

        _authToken = ResolveAuthToken();
    }

    /// <summary>True when an auth token is configured and backups can run.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_authToken);

    /// <summary>The remote endpoint (https base), safe to display (no secrets).</summary>
    public string Endpoint => _httpBase;

    /// <summary>Raised (on a background thread) after each backup attempt.</summary>
    public event Action<SyncStatus>? StatusChanged;

    private static string ToHttpBase(string url)
    {
        var s = url.Trim();
        if (s.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s.Substring("libsql://".Length);
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s.Substring("http://".Length);
        else if (!s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return s.TrimEnd('/');
    }

    private static string? ResolveAuthToken()
    {
        var token = EnvConfig.Get("TURSO_API_KEY", "TURSO_AUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            return token.Trim();

        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "turso_auth_token.txt");
            if (File.Exists(file))
            {
                var contents = File.ReadAllText(file).Trim();
                if (!string.IsNullOrWhiteSpace(contents))
                    return contents;
            }
        }
        catch
        {
            // Ignore token-file read errors; treated as "no token".
        }

        return null;
    }

    /// <summary>The interval currently used by the background backup loop.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>
    /// Starts a background loop that performs a backup shortly after startup and
    /// then every <paramref name="interval"/> (default 1 hour). Safe to call once.
    /// </summary>
    public void StartBackgroundSync(TimeSpan interval)
    {
        if (!Enabled)
        {
            Raise(new SyncStatus(false, false, DateTime.Now,
                "Cloud backup disabled - set TURSO_API_KEY (and TURSO_URL) to enable."));
            return;
        }

        _interval = interval;
        RestartLoop(immediateFirst: true);
    }

    /// <summary>
    /// Changes the automatic backup interval and reschedules the loop to use it.
    /// Does not trigger an immediate backup (the next one fires after the new
    /// interval elapses). No-op when backups are disabled.
    /// </summary>
    public void SetInterval(TimeSpan interval)
    {
        if (!Enabled || interval <= TimeSpan.Zero)
            return;

        _interval = interval;
        RestartLoop(immediateFirst: false);
        Raise(new SyncStatus(true, true, DateTime.Now,
            $"Auto-backup interval set to {DescribeInterval(interval)}."));
    }

    private void RestartLoop(bool immediateFirst)
    {
        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        var interval = _interval;

        _ = Task.Run(async () =>
        {
            try
            {
                if (immediateFirst)
                {
                    // Small delay so the first backup doesn't compete with app startup.
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    await SyncNowAsync(ct).ConfigureAwait(false);
                }

                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    await SyncNowAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal on shutdown or when the interval changes.
            }
        }, ct);
    }

    private static string DescribeInterval(TimeSpan t) =>
        t.TotalHours >= 1 && t.TotalMinutes % 60 == 0
            ? $"{(int)t.TotalHours} hour(s)"
            : $"{(int)t.TotalMinutes} min";

    /// <summary>Stops the background backup loop.</summary>
    public void Stop() => _loopCts?.Cancel();

    /// <summary>
    /// Performs one full backup now. Returns true on success. Never throws; the
    /// result is reported through <see cref="StatusChanged"/> and the return value.
    /// </summary>
    public async Task<bool> SyncNowAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            Raise(new SyncStatus(false, false, DateTime.Now,
                "Cloud backup disabled - no auth token."));
            return false;
        }

        // Only one backup at a time.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rowCount = await PushSnapshotAsync(ct).ConfigureAwait(false);
            Raise(new SyncStatus(true, true, DateTime.Now,
                $"Backed up {rowCount} row(s) to Turso."));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Raise(new SyncStatus(false, true, DateTime.Now, $"Backup failed: {ex.Message}"));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Pushes a complete snapshot inside a single remote transaction using an
    /// interactive (baton) stream: BEGIN, (re)create schema, clear tables,
    /// re-insert every row in chunks, COMMIT. Returns the number of rows written.
    /// </summary>
    private async Task<int> PushSnapshotAsync(CancellationToken ct)
    {
        string url = _httpBase;
        string? baton = null;

        // Phase 1: rebuild the remote tables from scratch. Dropping rather than
        // DELETEing guarantees the remote schema matches the local one even after a
        // local migration adds columns. The CREATE statements are read from the LIVE
        // local schema (not a static copy) so the recreated remote columns always
        // match the rows we insert below.
        //
        // foreign_keys is turned OFF (outside the transaction) so dropping a parent
        // table like Shops can't fail on a stale child row, and "Users" — an older
        // table this backup no longer manages — is dropped to clear historic drift.
        var setup = new List<object>
        {
            Exec("PRAGMA foreign_keys=OFF"),
            Exec("BEGIN"),
            Exec("DROP TABLE IF EXISTS Users"),
        };
        foreach (var table in Enumerable.Reverse(DatabaseService.BackupTables))
            setup.Add(Exec($"DROP TABLE IF EXISTS {table}"));
        foreach (var stmt in _db.GetBackupSchema())
            setup.Add(Exec(stmt));

        (url, baton) = await PostPipelineAsync(url, baton, setup, close: false, ct).ConfigureAwait(false);

        // Phase 2: re-insert all rows, chunked, on the same transaction.
        var rowsWritten = 0;
        var buffer = new List<object>();
        foreach (var table in DatabaseService.BackupTables)
        {
            var (columns, rows) = _db.ExportTable(table);
            if (columns.Count == 0)
                continue;

            var colList = string.Join(", ", columns);
            var placeholders = string.Join(", ", columns.Select(_ => "?"));
            var sql = $"INSERT INTO {table} ({colList}) VALUES ({placeholders})";

            foreach (var row in rows)
            {
                buffer.Add(Exec(sql, row));
                rowsWritten++;
                if (buffer.Count >= InsertChunkSize)
                {
                    (url, baton) = await PostPipelineAsync(url, baton, buffer, close: false, ct)
                        .ConfigureAwait(false);
                    buffer.Clear();
                }
            }
        }

        if (buffer.Count > 0)
            (url, baton) = await PostPipelineAsync(url, baton, buffer, close: false, ct).ConfigureAwait(false);

        // Phase 3: commit and close the stream.
        try
        {
            await PostPipelineAsync(url, baton, new List<object> { Exec("COMMIT") }, close: true, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(url, baton, ct).ConfigureAwait(false);
            throw;
        }

        return rowsWritten;
    }

    private async Task TryRollbackAsync(string url, string? baton, CancellationToken ct)
    {
        try
        {
            await PostPipelineAsync(url, baton, new List<object> { Exec("ROLLBACK") }, close: true, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort; the server discards the transaction when the stream closes.
        }
    }

    /// <summary>
    /// Sends one pipeline request and returns the (possibly updated) base URL and
    /// baton for the interactive stream. Throws if the HTTP call fails or any
    /// statement returns an error.
    /// </summary>
    private async Task<(string Url, string? Baton)> PostPipelineAsync(
        string url, string? baton, List<object> requests, bool close, CancellationToken ct)
    {
        var ops = new List<object>(requests);
        if (close)
            ops.Add(new Dictionary<string, object?> { ["type"] = "close" });

        var body = new Dictionary<string, object?> { ["requests"] = ops };
        if (baton is not null)
            body["baton"] = baton;

        var json = JsonSerializer.Serialize(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/v2/pipeline");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = text.Length > 200 ? text.Substring(0, 200) : text;
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} from Turso: {detail}");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // Surface the first statement-level error, if any.
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("type", out var t) && t.GetString() == "error")
                {
                    var msg = result.TryGetProperty("error", out var err)
                              && err.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : "unknown error";
                    throw new InvalidOperationException($"Turso statement error: {msg}");
                }
            }
        }

        var newBaton = root.TryGetProperty("baton", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()
            : null;
        var newUrl = root.TryGetProperty("base_url", out var bu) && bu.ValueKind == JsonValueKind.String
            ? bu.GetString()!.TrimEnd('/')
            : url;

        return (newUrl, newBaton);
    }

    // ----- Hrana request/value builders -----

    private static Dictionary<string, object?> Exec(string sql, object?[]? args = null)
    {
        var stmt = new Dictionary<string, object?> { ["sql"] = sql };
        if (args is not null)
            stmt["args"] = args.Select(EncodeValue).ToList();
        return new Dictionary<string, object?> { ["type"] = "execute", ["stmt"] = stmt };
    }

    private static Dictionary<string, object?> EncodeValue(object? value) => value switch
    {
        null or DBNull => new Dictionary<string, object?> { ["type"] = "null" },
        bool b => Integer(b ? 1 : 0),
        sbyte or byte or short or ushort or int or uint or long =>
            Integer(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        float f => Float(f),
        double d => Float(d),
        decimal m => Float((double)m),
        byte[] bytes => new Dictionary<string, object?> { ["type"] = "blob", ["base64"] = Convert.ToBase64String(bytes) },
        DateTime dt => Text(dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
        string s => Text(s),
        _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static Dictionary<string, object?> Integer(long v) =>
        new() { ["type"] = "integer", ["value"] = v.ToString(CultureInfo.InvariantCulture) };

    private static Dictionary<string, object?> Float(double v) =>
        new() { ["type"] = "float", ["value"] = v };

    private static Dictionary<string, object?> Text(string v) =>
        new() { ["type"] = "text", ["value"] = v };

    private void Raise(SyncStatus status)
    {
        try { StatusChanged?.Invoke(status); }
        catch { /* subscriber errors must not break the loop */ }
    }
}
