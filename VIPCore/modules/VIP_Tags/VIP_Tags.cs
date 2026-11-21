using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using VipCoreApi.Enums;

namespace VIP_Tags;

/// <summary>
/// Next-il: our own module. A VIP group's "ScoreboardTag" (plain clan tag) and "ChatTag" ({Color} syntax) from
/// vip.json, applied to every player of that group - players never pick them. Ported from our 1.x build
/// (Next-il/Plugins/VIPTags) to the 2.0 module API. Not upstream's VIP_Tag, which lets players type their own.
/// </summary>
public class VipTags : BasePlugin
{
    public override string ModuleAuthor => "ShiNxz, Next-il";
    public override string ModuleName => "[VIP] Tags";
    public override string ModuleVersion => "v3.0.0";

    private IVipCoreApi? _api;
    private TagFeature? _scoreboardTag;
    private TagFeature? _chatTag;

    // Per player, the tags of their current group
    private readonly Dictionary<ulong, TagData> _playerTags = [];

    public override void Load(bool hotReload)
    {
        AddCommandListener("say", OnPlayerChat, HookMode.Pre);
        AddCommandListener("say_team", OnPlayerChat, HookMode.Pre);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IVipCoreApi.Capability.Get();
        if (_api == null) return;

        // Hide: tags come from the group config, not from a menu choice
        _scoreboardTag = new TagFeature("ScoreboardTag", _api, this);
        _chatTag = new TagFeature("ChatTag", _api, this);

        if (!hotReload) return;

        foreach (var player in Utilities.GetPlayers().Where(p => !p.IsBot && _api.IsPlayerVip(p)))
            Cache(player);
    }

    public override void Unload(bool hotReload)
    {
        _scoreboardTag?.Dispose();
        _chatTag?.Dispose();
    }

    internal void Cache(CCSPlayerController player)
    {
        var data = new TagData(ReadGroupTag(player, "ScoreboardTag"), ReadGroupTag(player, "ChatTag"));
        _playerTags[player.SteamID] = data;
        ApplyScoreboardTag(player, data.ScoreboardTag);
    }

    internal void Forget(CCSPlayerController player)
    {
        if (!_playerTags.Remove(player.SteamID, out var data) || data.ScoreboardTag is null || !player.IsValid) return;

        // VIP gone or group changed: take the old clan tag off; a new group's tag is applied right after
        player.Clan = "";
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    internal void ApplyStored(CCSPlayerController player)
    {
        if (_playerTags.TryGetValue(player.SteamID, out var data))
            ApplyScoreboardTag(player, data.ScoreboardTag);
    }

    private string? ReadGroupTag(CCSPlayerController player, string feature)
    {
        try
        {
            var value = _api?.GetFeatureValue<string>(player, feature);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyScoreboardTag(CCSPlayerController player, string? tag)
    {
        if (string.IsNullOrEmpty(tag) || !player.IsValid) return;

        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || info.GetArg(1).Length == 0) return HookResult.Continue;

        var text = info.GetArg(1);

        // Command triggers ("!rank", "/menu") go through normal handling
        if (CoreConfig.SilentChatTrigger.Any(text.StartsWith) || CoreConfig.PublicChatTrigger.Any(text.StartsWith))
            return HookResult.Continue;

        if (_api?.IsPlayerVip(player) != true) return HookResult.Continue;

        // Only when the player's group defines a chat tag
        if (!_playerTags.TryGetValue(player.SteamID, out var data) || string.IsNullOrEmpty(data.ChatTag))
            return HookResult.Continue;

        var tag = ReplaceColors(data.ChatTag, player.Team);
        var message = $" {tag} {ChatColors.ForPlayer(player)}{player.PlayerName}{ChatColors.Default}: {text}";

        if (info.GetArg(0) == "say_team")
        {
            foreach (var target in Utilities.GetPlayers().Where(t => t.Team == player.Team && !t.IsBot))
                target.PrintToChat(message);
        }
        else
        {
            Server.PrintToChatAll(message);
        }

        return HookResult.Handled;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        foreach (var player in Utilities.GetPlayers())
            ApplyStored(player);

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { } player)
            _playerTags.Remove(player.SteamID);

        return HookResult.Continue;
    }

    // {ColorName} and {TeamColor} placeholders to chat color codes
    private static string ReplaceColors(string message, CsTeam team)
    {
        if (!message.Contains('{')) return message;

        var result = message;
        foreach (var field in typeof(ChatColors).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var pattern = $"{{{field.Name}}}";
            if (result.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                result = result.Replace(pattern, field.GetValue(null)!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return result.Replace("{TeamColor}", ChatColors.ForTeam(team).ToString());
    }

    private record TagData(string? ScoreboardTag, string? ChatTag);
}

/// <summary>
/// One registered feature per vip.json key, so the core knows the keys exist. Both share the plugin's cache; the
/// ScoreboardTag feature is the one that reacts to player events, so each event is handled once.
/// </summary>
public class TagFeature(string name, IVipCoreApi api, VipTags plugin) : VipFeature<string>(name, api, FeatureType.Hide)
{
    private bool Handles => Name == "ScoreboardTag";

    public override void OnPlayerAuthorized(CCSPlayerController player, string group)
    {
        if (Handles) plugin.Cache(player);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (Handles) plugin.Forget(player);
    }

    public override void OnPlayerSpawn(CCSPlayerController player, bool vip)
    {
        if (Handles && vip) plugin.ApplyStored(player);
    }
}
