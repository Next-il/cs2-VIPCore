using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using VIPCore.Configs;

namespace VIPCore.Services;

/// <summary>
/// Per-player settings (each feature's on/off state, the FOV a player picked, ...) in the MySQL table
/// <c>vip_cookies</c>, one row per player and key.
///
/// Next-il: upstream keeps them in one vip_core_cookie.json. On our servers that file is a single file shared by
/// every server on a node (the Mounts bind mount), and each server rewrote the whole of it at map end - the last
/// server to save erased everyone else's changes, and a read that hit a half-written file left a server with no
/// settings at all, which it then saved over the file. Rows are now read when a VIP loads and written per player.
///
/// Main thread only: rows are fetched off-thread with <see cref="FetchAsync"/> and handed in through
/// <see cref="Apply"/>, and writes are snapshotted on the main thread before they go off to the database.
/// </summary>
public class CookieService(Plugin plugin, DatabaseProvider dbProvider)
{
    private const string LegacyFileName = "vip_core_cookie.json";

    private readonly Dictionary<ulong, Dictionary<string, object>> _cookies = [];

    // Keys changed since the last write, per player
    private readonly Dictionary<ulong, HashSet<string>> _dirty = [];

    public void Set<T>(ulong steamId64, string key, T value)
    {
        if (!_cookies.TryGetValue(steamId64, out var values))
        {
            values = [];
            _cookies[steamId64] = values;
        }

        values[key] = value!;

        if (!_dirty.TryGetValue(steamId64, out var keys))
        {
            keys = [];
            _dirty[steamId64] = keys;
        }

        keys.Add(key);
    }

    public T Get<T>(ulong steamId64, string key)
    {
        return TryGet<T>(steamId64, key, out var value) ? value : default!;
    }

    public bool TryGet<T>(ulong steamId64, string key, out T value)
    {
        value = default!;

        if (!_cookies.TryGetValue(steamId64, out var values) || !values.TryGetValue(key, out var raw))
            return false;

        try
        {
            switch (raw)
            {
                case T typed:
                    value = typed;
                    return true;
                case JsonElement element:
                    value = element.Deserialize<T>(ConfigSystem.ConfigJsonOptions)!;
                    values[key] = value!;
                    return true;
                default:
                    value = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(raw), ConfigSystem.ConfigJsonOptions)!;
                    return true;
            }
        }
        catch (Exception e)
        {
            plugin.Logger.LogError("Failed to deserialize cookie value '{key}': {e}", key, e);
            return false;
        }
    }

    /// <summary>A player's saved values, read off the main thread. Empty when the read fails.</summary>
    public async Task<Dictionary<string, object>> FetchAsync(ulong steamId64)
    {
        var values = new Dictionary<string, object>();

        try
        {
            await using var connection = await dbProvider.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CookieRow>(
                "SELECT `key` AS `Key`, `value` AS `Value` FROM `vip_cookies` WHERE `steamid64` = @SteamId",
                new { SteamId = steamId64 });

            foreach (var row in rows)
            {
                try
                {
                    using var document = JsonDocument.Parse(row.Value);
                    values[row.Key] = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    plugin.Logger.LogWarning("Skipping unreadable cookie '{key}' of {steamId}", row.Key, steamId64);
                }
            }
        }
        catch (Exception e)
        {
            plugin.Logger.LogError("Failed to load cookies of {steamId}: {e}", steamId64, e.Message);
        }

        return values;
    }

    /// <summary>Main thread. Values set while the read was in flight are newer, so they are kept.</summary>
    public void Apply(ulong steamId64, Dictionary<string, object> loaded)
    {
        if (_cookies.TryGetValue(steamId64, out var current) && _dirty.TryGetValue(steamId64, out var keys))
        {
            foreach (var key in keys)
                if (current.TryGetValue(key, out var value))
                    loaded[key] = value;
        }

        _cookies[steamId64] = loaded;
    }

    /// <summary>
    /// Writes a player's changed values. <paramref name="forget"/> drops them from memory as well - on disconnect.
    /// Returns the write, so shutdown can wait for it.
    /// </summary>
    public Task Flush(ulong steamId64, bool forget = false)
    {
        var rows = new List<CookieWrite>();

        if (_dirty.Remove(steamId64, out var keys) && _cookies.TryGetValue(steamId64, out var values))
        {
            foreach (var key in keys)
            {
                if (values.TryGetValue(key, out var value))
                    rows.Add(new CookieWrite(steamId64, key, Serialize(value)));
            }
        }

        if (forget)
            _cookies.Remove(steamId64);

        return rows.Count == 0 ? Task.CompletedTask : Task.Run(() => WriteAsync(rows));
    }

    public Task FlushAll()
    {
        return Task.WhenAll(_dirty.Keys.ToList().Select(steamId => Flush(steamId)));
    }

    private static string Serialize(object value) =>
        value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value, value.GetType());

    private async Task WriteAsync(List<CookieWrite> rows)
    {
        try
        {
            await using var connection = await dbProvider.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO `vip_cookies` (`steamid64`, `key`, `value`) VALUES (@SteamId, @Key, @Value)
                ON DUPLICATE KEY UPDATE `value` = VALUES(`value`)
                """,
                rows);
        }
        catch (Exception e)
        {
            plugin.Logger.LogError("Failed to save {count} cookie value(s): {e}", rows.Count, e.Message);
        }
    }

    /// <summary>
    /// Creates the table when it is missing, and fills an empty one from the old vip_core_cookie.json once.
    /// Several servers starting together may all import - INSERT IGNORE makes that harmless. The JSON file is
    /// only read, never written; delete it by hand once every server runs this build.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await using var connection = await dbProvider.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS `vip_cookies` (
                    `steamid64` BIGINT UNSIGNED NOT NULL,
                    `key` VARCHAR(64) NOT NULL,
                    `value` TEXT NOT NULL,
                    `updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    PRIMARY KEY (`steamid64`, `key`)
                );
                """);

            if (await connection.ExecuteScalarAsync<int>("SELECT EXISTS (SELECT 1 FROM `vip_cookies` LIMIT 1)") == 1)
                return;

            var legacy = ReadLegacyFile();
            if (legacy.Count == 0) return;

            var rows = new List<CookieWrite>();
            foreach (var cookie in legacy)
            {
                foreach (var (key, value) in cookie.Features)
                    rows.Add(new CookieWrite(cookie.SteamId64, key, value.GetRawText()));
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(
                "INSERT IGNORE INTO `vip_cookies` (`steamid64`, `key`, `value`) VALUES (@SteamId, @Key, @Value)",
                rows, transaction);
            await transaction.CommitAsync();

            plugin.Logger.LogInformation("Imported {rows} cookie value(s) of {players} player(s) from {file}",
                rows.Count, legacy.Count, LegacyFileName);
        }
        catch (Exception e)
        {
            plugin.Logger.LogError("Cookie table setup failed: {e}", e);
        }
    }

    private List<LegacyCookie> ReadLegacyFile()
    {
        var path = Path.Combine(ConfigSystem.CoreConfigDirectory, LegacyFileName);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return [];

                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonSerializer.Deserialize<List<LegacyCookie>>(json, ConfigSystem.ConfigJsonOptions) ?? [];
            }
            catch (Exception e) when (e is JsonException or IOException && attempt < 4)
            {
            }
            catch (Exception e)
            {
                plugin.Logger.LogWarning("Could not read {file} for the import: {e}", LegacyFileName, e.Message);
                return [];
            }

            // A server still on the old build may be rewriting it right now
            Thread.Sleep(100);
        }

        return [];
    }

    private record CookieWrite(ulong SteamId, string Key, string Value);

    private class CookieRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private class LegacyCookie
    {
        public ulong SteamId64 { get; set; }
        public Dictionary<string, JsonElement> Features { get; set; } = new();
    }
}
