using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using VIPCore.Configs;
using VIPCore.Player;
using VIPCore.Services;

namespace VIPCore.Hud;

/// <summary>
/// Next-il: the VIP HUD - the welcome/status card (<see cref="StatusCard"/>, vip_hud.xml) and the !vip mouse menu
/// (<see cref="VipMenu"/>, vip_menu.xml). The layouts ship in the Next workshop addon.
///
/// <para>It replaces these chat lines: the welcome ("Hello, X! You are a VIP player. Your SUPREME status expires ...",
/// printed on every map change), group changed, time changed, expired, removed, the FOV lines and "You don't have
/// access to this command!" on !vip. <see cref="PlayersManager.NoticeHandler"/> hands it every status change; when it
/// returns false the old line is printed instead.</para>
///
/// <para>Hud.Enabled = false in vip_core.json, or a layout that fails to spawn: nothing here runs and every caller
/// keeps its chat line and the old center menu. Turning it on needs a map change or a plugin reload.</para>
///
/// <para>The welcome shows once per visit, at the first spawn after the VIP data loaded. A player back within
/// Hud.RejoinGraceSeconds is on the same visit - which also covers a map change, whether or not the engine reports a
/// disconnect for it.</para>
/// </summary>
public sealed class VipHud : IDisposable
{
    private const float WelcomeDelay = 0.5f;

    private readonly PlayersManager _players;
    private readonly Config<VipConfig> _coreConfig;

    private StatusCard? _card;
    private VipMenu? _menu;
    private bool _panoramaStarted;

    /// <summary>SteamID64 -> when they left; null while they are here. Present = welcomed on this visit.</summary>
    private readonly Dictionary<ulong, DateTime?> _visits = [];

    /// <summary>Slots waiting for their first spawn to see the welcome.</summary>
    private readonly HashSet<int> _pendingWelcome = [];

    private readonly Dictionary<int, ulong> _slotSteam = [];

    private TimeZoneInfo? _zone;
    private string? _zoneId;

    public VipHud(
        Plugin plugin,
        PlayersManager players,
        FeatureManager features,
        CookieService cookies,
        VipCoreApi api,
        Config<VipConfig> coreConfig,
        Config<GroupsConfig> groupsConfig)
    {
        Plugin = plugin;
        _players = players;
        Features = features;
        Cookies = cookies;
        Api = api;
        _coreConfig = coreConfig;
        Groups = groupsConfig;
    }

    internal Plugin Plugin { get; }
    internal FeatureManager Features { get; }
    internal CookieService Cookies { get; }
    internal VipCoreApi Api { get; }
    internal Config<GroupsConfig> Groups { get; }
    internal HudConfig Config => _coreConfig.Value.Hud;

    /// <summary>The !vip feature order (sort_menu.txt), kept by MenuManager.</summary>
    public string[] SortOrder { get; set; } = [];

    /// <summary>Running, and not switched off by a config reload since.</summary>
    public bool Enabled => _card != null && Config.Enabled;

    /// <summary>Spawns both layouts. Called once from Plugin.Load.</summary>
    public void Init()
    {
        if (!Config.Enabled) return;

        try
        {
            Panorama.Init(Plugin);
            _panoramaStarted = true;
            _card = new StatusCard(this);
            _menu = new VipMenu(this);
        }
        catch (Exception e)
        {
            // VIP works without its HUD: every caller falls back to chat when Enabled is false
            Plugin.Logger.LogWarning("VIP HUD failed to start, using chat instead: {e}", e.Message);
            _menu?.Dispose();
            _card?.Dispose();
            _menu = null;
            _card = null;
            return;
        }

        _players.NoticeHandler = OnNotice;
        Plugin.AddTimer(0.25f, () => _card?.Tick(), TimerFlags.REPEAT);
        Plugin.Logger.LogInformation("VIP HUD ready");
    }

    public void Dispose()
    {
        _players.NoticeHandler = null;
        _menu?.Dispose();
        _card?.Dispose();
        _menu = null;
        _card = null;

        if (_panoramaStarted) Panorama.Shutdown();
        _panoramaStarted = false;
    }

    // ------------------------------------------------------------------ hooks

    /// <summary>!vip. False when the HUD is off: the caller opens the old menu.</summary>
    public bool OpenMenu(CCSPlayerController player)
    {
        if (!Enabled || _menu == null) return false;

        _menu.Toggle(player);
        return true;
    }

    public void OnPlayerSpawn(CCSPlayerController player)
    {
        if (Enabled && _pendingWelcome.Contains(player.Slot))
            ScheduleWelcome(player);
    }

    public void OnClientDisconnect(int slot)
    {
        _pendingWelcome.Remove(slot);

        var steamId = _slotSteam.Remove(slot, out var known) ? known : Utilities.GetPlayerFromSlot(slot)?.SteamID ?? 0;
        if (steamId != 0 && _visits.ContainsKey(steamId))
            _visits[steamId] = DateTime.UtcNow;

        // Forget visits long over, so the table does not grow for the life of the server
        var stale = DateTime.UtcNow.AddSeconds(-Math.Max(60, Config.RejoinGraceSeconds) * 2);
        foreach (var old in _visits.Where(v => v.Value < stale).Select(v => v.Key).ToList())
            _visits.Remove(old);

        _card?.Forget(slot);
        _menu?.Forget(slot);
    }

    // ------------------------------------------------------------------ notices

    private bool OnNotice(VipNotice notice)
    {
        if (!Enabled) return false;

        var vip = notice.Player;
        var player = vip.Controller;
        if (!player.IsValid) return true;

        _slotSteam[player.Slot] = vip.SteamId.SteamId64;

        switch (notice.Kind)
        {
            case NoticeKind.Welcome:
                QueueWelcome(player, vip);
                break;

            case NoticeKind.NotVip:
                if (Config.WelcomeNonVips) QueueWelcome(player, vip);
                break;

            case NoticeKind.Activated:
                if (vip.Data is null) return false;
                Show(player, VipCard(vip, "new", T(player, "hud.card.activated", "VIP activated"), "", true));
                break;

            case NoticeKind.GroupChanged:
            {
                if (vip.Data is null) return false;
                var upgrade = Rank(notice.Group) > Rank(notice.OldGroup);
                Show(player, VipCard(vip, "up",
                    upgrade ? T(player, "hud.card.upgraded", "Upgraded") : T(player, "hud.card.changed", "Group changed"),
                    notice.OldGroup ?? "", upgrade));
                break;
            }

            case NoticeKind.TimeChanged:
                if (vip.Data is null) return false;
                Show(player, VipCard(vip, "welcome", ChangeText(player, notice.OldExpires, vip.Data.Expires), "", false));
                break;

            case NoticeKind.Expired:
                Show(player, GoneCard(player, notice.Group, T(player, "hud.card.ended", "VIP ended"),
                    T(player, "hud.card.ran_out", "Your {0} ran out", notice.Group)));
                break;

            case NoticeKind.Removed:
                Show(player, GoneCard(player, notice.Group, T(player, "hud.card.removed", "VIP removed"),
                    T(player, "hud.card.removed_title", "Your {0} was removed", notice.Group)));
                break;
        }

        _menu?.Refresh(player);
        return true;
    }

    private void QueueWelcome(CCSPlayerController player, VipPlayer vip)
    {
        var steamId = vip.SteamId.SteamId64;

        if (_visits.TryGetValue(steamId, out var left))
        {
            // Still here (a map change), or back within the grace: the same visit
            if (left == null || DateTime.UtcNow - left.Value < TimeSpan.FromSeconds(Config.RejoinGraceSeconds))
            {
                _visits[steamId] = null;
                return;
            }

            _visits.Remove(steamId);
        }

        _pendingWelcome.Add(player.Slot);

        // Loaded after the spawn already happened
        if (player.PawnIsAlive)
            ScheduleWelcome(player);
    }

    private void ScheduleWelcome(CCSPlayerController player)
    {
        var slot = player.Slot;
        var steamId = player.SteamID;

        Plugin.AddTimer(WelcomeDelay, () =>
        {
            if (!_pendingWelcome.Contains(slot)) return;

            var again = Utilities.GetPlayerFromSlot(slot);
            if (again is not { IsValid: true } || again.SteamID != steamId ||
                again.Connected != PlayerConnectedState.Connected || !_players.TryGetPlayer(again, out var vip))
                return;

            _pendingWelcome.Remove(slot);

            if (Member(again) != null)
                Show(again, VipCard(vip, "welcome", T(again, "hud.card.welcome", "Welcome back"), "", false));
            else if (Config.WelcomeNonVips)
                Show(again, NotVipCard(again));
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void Show(CCSPlayerController player, CardData card)
    {
        if (_card == null) return;

        _pendingWelcome.Remove(player.Slot);
        _visits[player.SteamID] = null;
        _card.Show(player, card, Config.CardSeconds);
    }

    // ------------------------------------------------------------------ cards

    private CardData VipCard(VipPlayer vip, string state, string eyebrow, string old, bool pop)
    {
        var player = vip.Controller;
        var data = vip.Data!;
        var perm = data.Expires == 0;
        var soon = IsSoon(data.Expires);

        return new CardData(
            state,
            HudFormat.Style(Config, data.Group),
            soon,
            perm,
            eyebrow,
            old,
            data.Group,
            player.PlayerName,
            soon ? T(player, "hud.card.renew", "Renew on {0} to keep {1}.", Config.Website, data.Group) : "",
            CardPerks(vip.Features),
            LeftText(player, data.Expires),
            perm ? "" : T(player, "hud.until", "until {0}", ShortDate(data.Expires)),
            StepOf(data.Expires),
            pop);
    }

    private CardData GoneCard(CCSPlayerController player, string group, string eyebrow, string title) => new(
        "gone",
        HudFormat.Style(Config, group),
        false,
        false,
        eyebrow,
        "",
        group,
        title,
        T(player, "hud.card.gone_sub", "Your bonuses stop now. Renew on {0}.", Config.Website),
        [],
        "",
        "",
        "p0",
        false);

    private CardData NotVipCard(CCSPlayerController player) => new(
        "gone",
        "none",
        false,
        false,
        T(player, "hud.card.not_vip_eyebrow", "Welcome"),
        "",
        T(player, "hud.card.not_vip", "NOT VIP"),
        player.PlayerName,
        T(player, "hud.card.not_vip_sub", "VIP adds XP and credits bonuses. Type !vip to compare the groups."),
        [],
        "",
        "",
        "p0",
        false);

    /// <summary>"XP x2", "CREDITS x2", "FOV 120", "35 MUSIC KITS", "SKYBOX" - the perks with a Short, in config order.</summary>
    private List<string> CardPerks(Dictionary<string, object>? features)
    {
        var perks = new List<string>();
        foreach (var perk in Config.Perks)
        {
            if (string.IsNullOrWhiteSpace(perk.Short)) continue;

            var value = HudFormat.Read(features, perk);
            if (!value.On) continue;

            try
            {
                perks.Add(string.Format(CultureInfo.InvariantCulture, perk.Short, value.Text));
            }
            catch (FormatException)
            {
                perks.Add(perk.Short);
            }
        }

        return perks;
    }

    private string ChangeText(CCSPlayerController player, long oldExpires, long expires)
    {
        if (expires == 0)
            return oldExpires == 0
                ? T(player, "hud.card.time_changed", "VIP time changed")
                : T(player, "hud.card.permanent_now", "Now permanent");

        return oldExpires != 0 && expires > oldExpires
            ? T(player, "hud.card.extended", "Extended +{0}", Duration(player, expires - oldExpires))
            : T(player, "hud.card.time_changed", "VIP time changed");
    }

    /// <summary>A group's place in vip.json (VIP, PREMIUM, SUPREME): a higher one is an upgrade.</summary>
    private int Rank(string? group) => group == null ? -1 : Groups.Value.Keys.ToList().IndexOf(group);

    // ------------------------------------------------------------------ shared with the menu

    /// <summary>The player's VIP, when they have it right now; null otherwise (and while css_vip_enable is 0).</summary>
    internal VipPlayer? Member(CCSPlayerController player) =>
        _players.TryGetPlayer(player, out var vip) && vip.IsVip && Api.IsPlayerVip(player) ? vip : null;

    internal bool IsSoon(long expires) =>
        expires != 0 && expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= Config.SoonDays * 86400L;

    /// <summary>The bar: full at Hud.BarDays left or more, and for a permanent status.</summary>
    internal string StepOf(long expires)
    {
        if (expires == 0) return "p" + HudFormat.Steps;

        var left = expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return HudFormat.Step(left / (Math.Max(1, Config.BarDays) * 86400.0));
    }

    /// <summary>"28 days left", "5 hours left", "Permanent".</summary>
    internal string LeftText(CCSPlayerController player, long expires) => expires == 0
        ? T(player, "hud.permanent", "Permanent")
        : T(player, "hud.left", "{0} left",
            Duration(player, Math.Max(0, expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds())));

    private string Duration(CCSPlayerController player, long seconds)
    {
        if (seconds >= 86400)
        {
            var days = seconds / 86400;
            return days == 1 ? T(player, "hud.day", "1 day") : T(player, "hud.days", "{0} days", days);
        }

        if (seconds >= 3600)
        {
            var hours = seconds / 3600;
            return hours == 1 ? T(player, "hud.hour", "1 hour") : T(player, "hud.hours", "{0} hours", hours);
        }

        return T(player, "hud.minutes", "{0} min", Math.Max(1, seconds / 60));
    }

    /// <summary>"20 Dec", in Hud.TimeZone.</summary>
    private string ShortDate(long unix) => Local(unix).ToString("d MMM", CultureInfo.InvariantCulture);

    /// <summary>"20 Dec 2026", in Hud.TimeZone.</summary>
    internal string FullDate(long unix) => Local(unix).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private DateTimeOffset Local(long unix)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unix);

        if (_zoneId != Config.TimeZone)
        {
            _zoneId = Config.TimeZone;
            try
            {
                _zone = TimeZoneInfo.FindSystemTimeZoneById(_zoneId);
            }
            catch (Exception)
            {
                Plugin.Logger.LogWarning("Hud.TimeZone '{zone}' is not a known time zone, using UTC", _zoneId);
                _zone = null;
            }
        }

        return _zone == null ? time : TimeZoneInfo.ConvertTime(time, _zone);
    }

    /// <summary>A phrase from lang/, or the English fallback when the player's lang file does not have it yet.</summary>
    internal string T(CCSPlayerController player, string key, string fallback, params object[] args)
    {
        var text = Plugin.Localizer.ForPlayer(player, key, args);
        return string.IsNullOrEmpty(text) || text == key
            ? string.Format(CultureInfo.InvariantCulture, fallback, args)
            : text;
    }
}
