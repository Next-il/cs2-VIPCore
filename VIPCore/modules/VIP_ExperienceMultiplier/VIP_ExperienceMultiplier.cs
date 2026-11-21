using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RanksApi;
using VipCoreApi;
using VipCoreApi.Args;
using VipCoreApi.Enums;

namespace VIP_ExperienceMultiplier;

// Next-il: our own module - the group's exp_multiplier for VIPs, plus a "Name Reward" for anyone with the server
// name in their nickname. Ported from our 1.x build to the 2.0 module API.
public static class ConVars
{
    public static readonly FakeConVar<float> NameRewardMultiplier =
        new("ranks_namereward_experience_multiplier", "The multiplier for player experience", 1.5f);

    public static readonly FakeConVar<string> NameRewardName =
        new("ranks_namereward_name", "The string to give the bonus for", "next-il");
}

public class VipExperienceMultiplier : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius, Next-il";
    public override string ModuleName => "[VIP] Experience Multiplier";
    public override string ModuleVersion => "v2.0.0-next";

    private ExperienceMultiplier? _experienceMultiplier;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        RegisterFakeConVars(typeof(ConVars));

        var vipApi = IVipCoreApi.Capability.Get();
        if (vipApi == null) return;

        var ranksApi = IRanksApi.Capability.Get();
        if (ranksApi is null) return;

        _experienceMultiplier = new ExperienceMultiplier(this, vipApi, ranksApi);
    }

    public override void Unload(bool hotReload)
    {
        _experienceMultiplier?.Dispose();
    }
}

public class ExperienceMultiplier : VipFeature<float>
{
    private readonly BasePlugin _plugin;
    private readonly float[] _multiplier = Enumerable.Repeat(1f, 70).ToArray();

    // One log line per player per map, so a live check can see the bonus land without flooding the log
    private readonly HashSet<ulong> _logged = [];

    public ExperienceMultiplier(BasePlugin plugin, IVipCoreApi api, IRanksApi ranksApi)
        : base("exp_multiplier", api, FeatureType.Hide)
    {
        _plugin = plugin;
        ranksApi.PlayerExperienceChanged += OnExperienceChanged;

        plugin.RegisterListener<Listeners.OnMapStart>(_ => _logged.Clear());
        plugin.RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            if (@event.Userid is { } player)
                _multiplier[player.Slot] = 1;
            return HookResult.Continue;
        });
    }

    private int? OnExperienceChanged(CCSPlayerController player, int experience)
    {
        // Lost experience is never multiplied
        if (experience <= 0 || !player.IsValid) return experience;

        var bonuses = new List<(string Label, float Multiplier)>();

        var vip = _multiplier[player.Slot];
        if (vip > 1f)
            bonuses.Add(($"VIP ({ChatColors.Blue}x{Format(vip)}{ChatColors.Default})", vip));

        var name = ConVars.NameRewardName.Value?.Trim() ?? "";
        if (name.Length > 0 && player.PlayerName.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            var reward = ConVars.NameRewardMultiplier.Value;
            bonuses.Add(($"Name Reward ({ChatColors.Blue}x{Format(reward)}{ChatColors.Default})", reward));
        }

        if (bonuses.Count == 0) return experience;

        // The 1.x build added the multipliers up as whole numbers ((int)1.3 + ...), so x1.3 and x1.5 gave
        // nothing and only x2 worked. They are multiplied now: VIP x1.5 with the name reward x1.5 is x2.25.
        var total = bonuses.Aggregate(1f, (product, bonus) => product * bonus.Multiplier);
        var result = (int)Math.Round(experience * total, MidpointRounding.AwayFromZero);

        var labels = string.Join(", ", bonuses.Select(b => b.Label));
        Server.NextFrame(() =>
        {
            if (player.IsValid)
                player.PrintToChat($" {ChatColors.Lime}➜{ChatColors.Default} Active XP Bonus: {labels}");
        });

        if (_logged.Add(player.SteamID))
            _plugin.Logger.LogInformation("XP bonus x{total} for {name} ({steamId}): +{from} -> +{to}",
                Format(total), player.PlayerName, player.SteamID, experience, result);

        return result;
    }

    public override void OnPlayerAuthorized(CCSPlayerController player, string group) => UpdateMultiplier(player);

    public override void OnPlayerRemoved(CCSPlayerController player, string group) => _multiplier[player.Slot] = 1;

    public override void OnSelectItem(CCSPlayerController player, VipFeature feature) => UpdateMultiplier(player);

    public override void OnPlayerSpawn(CCSPlayerController player, bool vip) => UpdateMultiplier(player);

    private void UpdateMultiplier(CCSPlayerController player)
    {
        _multiplier[player.Slot] = IsFeatureEnabled(player) ? GetValue(player) : 1;
    }

    private static string Format(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
