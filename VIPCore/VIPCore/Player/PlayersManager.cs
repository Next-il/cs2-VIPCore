using System.Diagnostics.CodeAnalysis;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using VIPCore.Configs;
using VIPCore.Services;
using VipCoreApi;
using VipCoreApi.Enums;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace VIPCore.Player;

public class PlayersManager
{
    private readonly Plugin _plugin;
    private readonly DatabaseService _database;
    private readonly CookieService _cookies;
    private readonly FeatureManager _featureManager;
    private readonly Config<GroupsConfig> _groupsConfig;
    private readonly Config<VipConfig> _coreConfig;

    private readonly VipPlayer?[] _players = new VipPlayer?[65];

    public event Action<VipPlayer>? PlayerLoaded;
    public event Action<VipPlayer, string>? PlayerRemoved;
    public event Action<VipPlayer>? PlayerDisconnect;

    /// <summary>
    /// Next-il: the HUD (Hud/VipHud.cs). Gets every status change first; true means it showed it on the card, false
    /// or no handler means the old chat line is printed instead - which is all of them when Hud.Enabled is false.
    /// </summary>
    public Func<VipNotice, bool>? NoticeHandler { get; set; }

    private bool Notify(VipNotice notice)
    {
        if (NoticeHandler is not { } handler) return false;

        try
        {
            return handler(notice);
        }
        catch (Exception e)
        {
            _plugin.Logger.LogError("VIP HUD failed on {kind}, using chat instead: {e}", notice.Kind, e);
            return false;
        }
    }

    public PlayersManager(
        Plugin plugin,
        DatabaseService database,
        CookieService cookies,
        FeatureManager featureManager,
        Config<GroupsConfig> groupsConfig,
        Config<VipConfig> coreConfig)
    {
        _plugin = plugin;
        _database = database;
        _cookies = cookies;
        _featureManager = featureManager;
        _groupsConfig = groupsConfig;
        _coreConfig = coreConfig;

        featureManager.FeatureRegistered += feature =>
        {
            foreach (var player in Players)
                ApplyFeatureState(player, feature);
        };
    }

    public IEnumerable<VipPlayer> Players => _players.OfType<VipPlayer>();

    public bool TryGetPlayer(CCSPlayerController player, [NotNullWhen(true)] out VipPlayer? vipPlayer)
    {
        vipPlayer = null;
        if (player is not { IsValid: true }) return false;

        var slot = player.Slot;
        vipPlayer = slot >= 0 && slot < _players.Length ? _players[slot] : null;
        return vipPlayer != null;
    }

    public void OnClientAuthorized(int slot, SteamID steamId)
    {
        var controller = Utilities.GetPlayerFromSlot(slot);
        if (controller is null || !controller.IsValid || controller.IsBot) return;

        if (_players[slot] is { } previous)
            previous.Disconnected = true;

        var player = new VipPlayer(controller, steamId);
        _players[slot] = player;

        Reload(player);
    }

    public void OnClientDisconnect(int slot)
    {
        var player = _players[slot];
        if (player is null) return;

        if (player.IsVip)
        {
            foreach (var feature in _featureManager.GetFeatures().Where(f => f.Type is FeatureType.Toggle))
            {
                if (player.FeatureStates.TryGetValue(feature.Name, out var state))
                    _cookies.Set(player.SteamId.SteamId64, feature.Name, (int)state);
            }
        }

        _cookies.Flush(player.SteamId.SteamId64, forget: true);

        PlayerDisconnect?.Invoke(player);

        player.Disconnected = true;
        _players[slot] = null;

        if (player.Data is null || player.IsTemporary) return;

        var accountId = player.SteamId.AccountId;
        var name = player.Controller.IsValid ? player.Controller.PlayerName : player.Data.Name;
        Task.Run(() => _database.UpdateLastVisitAsync(accountId, name));
    }

    public void LoadConnectedPlayers()
    {
        foreach (var controller in Utilities.GetPlayers())
        {
            if (controller.IsBot || controller.AuthorizedSteamID is not { } steamId) continue;

            OnClientAuthorized(controller.Slot, steamId);
        }
    }

    public void ReloadAll()
    {
        foreach (var player in Players)
            Reload(player);
    }

    public void Reload(VipPlayer player)
    {
        Task.Run(() => LoadAsync(player));
    }

    private async Task LoadAsync(VipPlayer player)
    {
        // Next-il: a grant, change or removal made while this read was in flight (admin menu, the database sync)
        // is newer than what the read saw, so the read's result is dropped instead of undoing it.
        var generation = player.Generation;

        try
        {
            var accountId = player.SteamId.AccountId;

            // Next-il: no DELETE of the expired row first - GetUserAsync already skips expired rows, and the
            // website's hourly job removes them, so that was one more round trip on every connect.
            var data = await _database.GetUserAsync(accountId);
            var cookies = data is null ? null : await _cookies.FetchAsync(player.SteamId.SteamId64);

            await Server.NextFrameAsync(() =>
            {
                if (player.Disconnected || !player.Controller.IsValid) return;
                if (player.Generation != generation) return;

                player.Loaded = true;

                if (cookies != null)
                    _cookies.Apply(player.SteamId.SteamId64, cookies);

                if (data is null)
                {
                    if (!player.IsTemporary)
                        Remove(player);
                    if (!player.IsVip)
                        Notify(new VipNotice(player, NoticeKind.NotVip, ""));
                    return;
                }

                // Rows the website writes carry no name
                if (string.IsNullOrEmpty(data.Name))
                    data.Name = player.Controller.PlayerName;

                SetVip(player, data, isTemporary: false);

                // Next-il: the HUD shows it once per visit at first spawn; the chat line goes out on every map change
                if (!Notify(new VipNotice(player, NoticeKind.Welcome, data.Group)))
                    PrintWelcomeWhenConnected(player);
            });
        }
        catch (Exception e)
        {
            _plugin.Logger.LogError("Failed to load VIP data of {steamId}: {e}", player.SteamId.SteamId64, e);
        }
    }

    public void SetVip(VipPlayer player, VipData data, bool isTemporary)
    {
        player.Generation++;
        player.Clear();
        player.Data = data;
        player.IsTemporary = isTemporary;
        player.Features = ResolveGroup(data.Group);

        ApplyFeatureStates(player);

        if (player.IsVip)
            PlayerLoaded?.Invoke(player);
    }

    public bool Remove(VipPlayer player, bool notify = false)
    {
        if (player.Data is null) return false;

        var group = player.Data.Group;
        player.Generation++;
        PlayerRemoved?.Invoke(player, group);
        player.Clear();

        if (notify && !Notify(new VipNotice(player, NoticeKind.Removed, group)))
            _plugin.PrintToChat(player.Controller, _plugin.Translate(player.Controller, "vip.Removed", group));

        return true;
    }

    public void RefreshGroups()
    {
        foreach (var player in Players.Where(p => p.Data != null))
        {
            player.Features = ResolveGroup(player.Group);
            player.FeatureValues.Clear();
            ApplyFeatureStates(player);
        }
    }

    public void RemoveExpired()
    {
        foreach (var player in Players.ToList())
        {
            if (player.Data is not { IsExpired: true } data) continue;

            Remove(player);

            if (!Notify(new VipNotice(player, NoticeKind.Expired, data.Group)))
                _plugin.PrintToChat(player.Controller, _plugin.Translate(player.Controller, "vip.Expired", data.Group));
            _plugin.LogInfo("User '{name} [{accountId}]' has been removed due to expired VIP status.", data.Name,
                data.AccountId);

            // Next-il: the row is left alone. The website's hourly job deletes expired rows, and a renewal bought
            // a moment before expiry may already have moved it forward - SyncWithDatabase gives it back then.
        }
    }

    /// <summary>
    /// Next-il: picks up VIP granted, changed or revoked outside the game - the website writes vip_users - for
    /// players who are already online; upstream only reads the database when a player connects. One query for all
    /// online players, from the 30s timer. Replaces the separate VipDbChecker plugin, which pulled every row of the
    /// table every 10 seconds on every server and never noticed a group change or an extension.
    /// </summary>
    public void SyncWithDatabase()
    {
        // Snapshot on the main thread; only players whose own load has finished and who are not on temporary VIP
        var snapshot = Players
            .Where(p => p is { Disconnected: false, Loaded: true, IsTemporary: false } && p.Controller.IsValid)
            .Select(p => new SyncEntry(p, p.Generation, p.IsVip))
            .ToList();
        if (snapshot.Count == 0) return;

        var accountIds = snapshot.Select(e => e.Player.SteamId.AccountId).Distinct().ToArray();

        Task.Run(async () =>
        {
            try
            {
                var rows = (await _database.GetUsersAsync(accountIds)).ToDictionary(r => r.AccountId);

                // Players about to become VIP need their saved settings first
                var cookies = new Dictionary<ulong, Dictionary<string, object>>();
                foreach (var entry in snapshot.Where(e => !e.WasVip && rows.ContainsKey(e.Player.SteamId.AccountId)))
                    cookies[entry.Player.SteamId.SteamId64] = await _cookies.FetchAsync(entry.Player.SteamId.SteamId64);

                await Server.NextFrameAsync(() =>
                {
                    foreach (var entry in snapshot)
                        ApplySync(entry, rows.GetValueOrDefault(entry.Player.SteamId.AccountId), cookies);
                });
            }
            catch (Exception e)
            {
                _plugin.Logger.LogError("VIP database sync failed: {e}", e.Message);
            }
        });
    }

    private void ApplySync(SyncEntry entry, VipData? row, Dictionary<ulong, Dictionary<string, object>> cookies)
    {
        var player = entry.Player;

        // Gone, or changed in game since the snapshot - that change is newer than what the query saw
        if (player.Disconnected || !player.Controller.IsValid || player.Generation != entry.Generation) return;
        if (player.IsTemporary) return;

        var controller = player.Controller;

        if (row is null)
        {
            if (!player.IsVip) return;

            _plugin.LogInfo("VIP of '{name} [{accountId}]' was removed in the database - removing it in game",
                player.Data.Name, player.Data.AccountId);
            Remove(player, notify: true);
            return;
        }

        if (!player.IsVip)
        {
            if (cookies.TryGetValue(player.SteamId.SteamId64, out var values))
                _cookies.Apply(player.SteamId.SteamId64, values);

            if (string.IsNullOrEmpty(row.Name))
                row.Name = controller.PlayerName;

            _plugin.LogInfo("'{name} [{accountId}]' got {group} in the database - applying it in game",
                controller.PlayerName, row.AccountId, row.Group);
            SetVip(player, row, isTemporary: false);
            if (!Notify(new VipNotice(player, NoticeKind.Activated, row.Group)))
                PrintWelcome(player);
            return;
        }

        var data = player.Data;

        if (row.Group != data.Group)
        {
            _plugin.LogInfo("VIP group of '{name} [{accountId}]' changed in the database: {old} -> {group}",
                data.Name, data.AccountId, data.Group, row.Group);
            if (string.IsNullOrEmpty(row.Name))
                row.Name = data.Name;

            var oldGroup = data.Group;
            var oldExpires = data.Expires;
            PlayerRemoved?.Invoke(player, oldGroup);
            SetVip(player, row, isTemporary: false);
            if (!Notify(new VipNotice(player, NoticeKind.GroupChanged, row.Group, oldGroup, oldExpires)))
                _plugin.PrintToChat(controller, _plugin.Translate(controller, "vip.GroupChanged", row.Group));
            return;
        }

        if (row.Expires != data.Expires)
        {
            _plugin.LogInfo("VIP expiry of '{name} [{accountId}]' changed in the database: {old} -> {expires}",
                data.Name, data.AccountId, data.Expires, row.Expires);
            var oldExpires = data.Expires;
            data.Expires = row.Expires;
            player.Generation++;
            if (!Notify(new VipNotice(player, NoticeKind.TimeChanged, data.Group, null, oldExpires)))
                _plugin.PrintToChat(controller,
                    $"{_plugin.Translate(controller, "vip.TimeChanged")} {ExpiresText(controller, data.Expires)}");
        }
    }

    private record SyncEntry(VipPlayer Player, int Generation, bool WasVip);

    public void GiveVip(CCSPlayerController? controller, VipData data, bool isTemporary = false, bool notify = false)
    {
        if (!isTemporary)
            Task.Run(() => _database.UpsertUserAsync(data));

        if (controller is null || !TryGetPlayer(controller, out var player)) return;

        SetVip(player, data, isTemporary);

        if (notify && !Notify(new VipNotice(player, NoticeKind.Activated, data.Group)))
            PrintWelcome(player);
    }

    public void UpdateVip(int accountId, CCSPlayerController? controller, string? name, string? group, long? expires,
        bool notify = false)
    {
        if (controller != null && TryGetPlayer(controller, out var player) && player.Data is { } data)
        {
            if (name != null)
                data.Name = name;

            var oldExpires = data.Expires;
            string? oldGroup = null;

            if (group != null && group != data.Group)
            {
                // Next-il: modules hear that the old group is gone (VIP_Flags takes its flags back) before the
                // new one loads - upstream only fired the load.
                oldGroup = data.Group;
                PlayerRemoved?.Invoke(player, data.Group);
                data.Group = group;
                SetVip(player, data, player.IsTemporary);
            }

            if (expires != null)
            {
                data.Expires = expires.Value;
                player.Generation++;
            }

            // Next-il: one card for both changes (the upgrade card shows the new days left too); the chat
            // fallback keeps its two lines
            if (notify)
            {
                var shown = oldGroup != null
                    ? Notify(new VipNotice(player, NoticeKind.GroupChanged, data.Group, oldGroup, oldExpires))
                    : expires != null && Notify(new VipNotice(player, NoticeKind.TimeChanged, data.Group, null, oldExpires));

                if (!shown)
                {
                    if (oldGroup != null)
                        _plugin.PrintToChat(controller, _plugin.Translate(controller, "vip.GroupChanged", data.Group));
                    if (expires != null)
                        _plugin.PrintToChat(controller,
                            $"{_plugin.Translate(controller, "vip.TimeChanged")} {ExpiresText(controller, data.Expires)}");
                }
            }

            if (player.IsTemporary) return;
        }

        Task.Run(() => _database.UpdateUserAsync(accountId, name, group, expires));
    }

    public void RemoveVip(int accountId, CCSPlayerController? controller, bool notify = false)
    {
        var player = controller != null && TryGetPlayer(controller, out var found) ? found : null;
        var isTemporary = player is { IsTemporary: true };

        if (player != null)
            Remove(player, notify);

        if (!isTemporary)
            Task.Run(() => _database.RemoveUserAsync(accountId));
    }

    public VipData CreateVipData(int accountId, string name, string group, long expires) => new()
    {
        AccountId = accountId,
        Name = name,
        Group = group,
        Expires = expires,
        LastVisit = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ServerId = _coreConfig.Value.ServerId
    };

    public void PrintWelcome(VipPlayer player)
    {
        if (player.Data is not { } data) return;

        var controller = player.Controller;
        var welcome = _plugin.Translate(controller, "vip.WelcomeToTheServer", data.Name).TrimEnd();

        _plugin.PrintToChat(controller, data.Expires == 0
            ? welcome
            : $"{welcome} {_plugin.Translate(controller, "vip.Expires", data.Group, FormatTime(data.Expires))}");
    }

    private void PrintWelcomeWhenConnected(VipPlayer player)
    {
        Timer? timer = null;
        timer = _plugin.AddTimer(1.0f, () =>
        {
            if (player.Disconnected || !player.Controller.IsValid)
            {
                timer?.Kill();
                return;
            }

            if (player.Controller.Connected != PlayerConnectedState.Connected) return;

            timer?.Kill();
            PrintWelcome(player);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private string ExpiresText(CCSPlayerController controller, long expires)
    {
        return expires == 0
            ? _plugin.Translate(controller, "vip.Permanent")
            : _plugin.Translate(controller, "vip.Until", FormatTime(expires));
    }

    private static string FormatTime(long unixTime)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixTime).ToString("G");
    }

    private Dictionary<string, object>? ResolveGroup(string group)
    {
        try
        {
            var features = _groupsConfig.Value.ResolveGroup(group);
            if (features is null)
                _plugin.LogWarning("VIP group '{group}' was not found in vip.json", group);

            return features;
        }
        catch (Exception e)
        {
            _plugin.Logger.LogError("Failed to resolve VIP group '{group}': {e}", group, e.Message);
            return null;
        }
    }

    private void ApplyFeatureStates(VipPlayer player)
    {
        player.FeatureStates.Clear();

        foreach (var feature in _featureManager.GetFeatures())
            ApplyFeatureState(player, feature);
    }

    private void ApplyFeatureState(VipPlayer player, VipFeature feature)
    {
        if (player.Data is null) return;

        if (!player.HasFeature(feature.Name))
        {
            player.FeatureStates[feature.Name] = FeatureState.NoAccess;
            return;
        }

        var state = _cookies.TryGet<int>(player.SteamId.SteamId64, feature.Name, out var cookie)
            ? (FeatureState)cookie
            : feature.State;

        player.FeatureStates[feature.Name] = state is FeatureState.NoAccess ? FeatureState.Enabled : state;
    }
}
