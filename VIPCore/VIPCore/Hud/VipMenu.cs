using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using VIPCore.Configs;
using VIPCore.Player;
using VipCoreApi;
using VipCoreApi.Args;
using VipCoreApi.Enums;

namespace VIPCore.Hud;

/// <summary>
/// !vip on a mouse-driven layout (vip_menu.xml). VIPs get their status, four perk tiles and one settings row per
/// thing they can change; everyone else gets the groups side by side and where to buy.
///
/// <para><b>The rows are generic.</b> They come from what is loaded, not from the layout: every registered VIP feature
/// the player's group has and that is not hidden gets a row - its <see cref="VipFeature.GetMenuOptions"/> as buttons
/// (FOV), ON/OFF for a toggle (TAG), or a button that opens the module's own menu. Then the configured links
/// (Hud.Links: !music, !sky). A new module needs no new layout until the six-row pool runs out.</para>
///
/// <para>Clicks arrive by element id (PanelAction.Button) and are checked again here - the player is still VIP, the
/// group still has the feature, the option is not locked - because they come from a client.</para>
/// </summary>
internal sealed class VipMenu : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/vip_menu.vxml_c";
    private const string Root = "VipMenuRoot";
    private const string Card = "vm";
    private const int RowSlots = 6;
    private const int OptionSlots = 8;
    private const int TileSlots = 4;
    private const int CompareRows = 8;
    private const int CompareColumns = 4;

    private abstract record Row(string Label);

    /// <summary>Buttons: a feature's own options, or ON/OFF for a toggle.</summary>
    private sealed record OptionsRow(string Label, VipFeature Feature, IReadOnlyList<FeatureOption> Options, bool Toggle)
        : Row(Label);

    /// <summary>A selectable feature without options: opens its own menu, as the old !vip did.</summary>
    private sealed record OpenRow(string Label, VipFeature Feature) : Row(Label);

    /// <summary>Another plugin's menu (Hud.Links).</summary>
    private sealed record LinkRow(string Label, HudLink Link) : Row(Label);

    private sealed class Viewer
    {
        public List<Row> Rows = [];
    }

    private readonly VipHud _hud;
    private readonly PanelHandle _panel;
    private readonly ClassTracker _classes = new();
    private readonly Dictionary<int, Viewer> _viewers = [];

    public VipMenu(VipHud hud)
    {
        _hud = hud;
        _panel = Panorama.Spawn(Layout, new LayoutContract
        {
            RootPanelId = Root,
            RevealClass = "show",
            CloseButtonId = "vm_close",
            RowCount = 0,
            CaptureInput = true,
            HideHud = HideHudFlags.None,
            SharedText = false,
        });
        _panel.OnEvent += OnEvent;
    }

    public void Dispose()
    {
        _panel.Dispose();
        _viewers.Clear();
    }

    /// <summary>!vip: opens the menu, or closes it when it is already open - there is no Escape key.</summary>
    public void Toggle(CCSPlayerController player)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false }) return;

        if (_panel.IsOpenFor(player))
        {
            _panel.Close(player);
            return;
        }

        _panel.Open(player);
        if (!_panel.IsOpenFor(player)) return;

        _classes.Forget(player.Slot);
        var viewer = _viewers[player.Slot] = new Viewer();
        Draw(player, viewer);
    }

    /// <summary>Redraws an open menu after the player's VIP changed.</summary>
    public void Refresh(CCSPlayerController player)
    {
        if (_panel.IsOpenFor(player) && _viewers.TryGetValue(player.Slot, out var viewer))
            Draw(player, viewer);
    }

    public void Forget(int slot)
    {
        _panel.Close(slot);
        _classes.Forget(slot);
        _viewers.Remove(slot);
    }

    // ------------------------------------------------------------------ drawing

    private void Draw(CCSPlayerController player, Viewer viewer)
    {
        var vip = _hud.Member(player);

        _classes.Set(_panel, player, Card, "g-" + (vip != null ? HudFormat.Style(_hud.Config, vip.Group) : "none"),
            HudFormat.StyleClasses);
        Set(player, "vm_tag", vip?.Group ?? _hud.T(player, "hud.menu.not_member", "NOT A MEMBER"));
        Cls(player, "vm_member", "hidden", vip == null);
        Cls(player, "vm_compare", "hidden", vip != null);

        if (vip != null)
        {
            DrawMember(player, viewer, vip);
        }
        else
        {
            viewer.Rows = [];
            DrawCompare(player);
        }
    }

    private void DrawMember(CCSPlayerController player, Viewer viewer, VipPlayer vip)
    {
        var config = _hud.Config;
        var expires = vip.Data!.Expires;
        var perm = expires == 0;

        Set(player, "vm_gname", vip.Group);
        if (perm)
        {
            Set(player, "vm_until_a", _hud.T(player, "hud.permanent", "Permanent"));
            Set(player, "vm_until_b", "");
            Set(player, "vm_until_c", "");
        }
        else
        {
            Set(player, "vm_until_a", _hud.T(player, "hud.menu.until", "Until"));
            Set(player, "vm_until_b", _hud.FullDate(expires));
            Set(player, "vm_until_c", "- " + _hud.LeftText(player, expires));
        }

        Cls(player, Card, "perm", perm);
        Cls(player, Card, "soon", _hud.IsSoon(expires));
        _classes.Set(_panel, player, "vm_fill", _hud.StepOf(expires), HudFormat.StepClasses);

        // Perk tiles
        var tiles = config.Perks.Where(p => p.Tile).Take(TileSlots).ToList();
        for (var i = 0; i < TileSlots; i++)
        {
            var id = $"vm_t{i}";
            if (i >= tiles.Count)
            {
                Cls(player, id, "hidden", true);
                continue;
            }

            var perk = tiles[i];
            var value = HudFormat.Read(vip.Features, perk);
            Set(player, $"{id}_k", perk.Label);
            Set(player, $"{id}_v", !value.On ? "-" : HudFormat.IsFlag(perk) ? _hud.T(player, "hud.on", "ON") : value.Text);
            Cls(player, id, "off", !value.On);
            Cls(player, id, "hidden", false);
        }

        // Settings rows
        viewer.Rows = BuildRows(player, vip);
        for (var i = 0; i < RowSlots; i++)
        {
            var id = $"vm_r{i}";
            if (i >= viewer.Rows.Count)
            {
                Cls(player, id, "hidden", true);
                continue;
            }

            var row = viewer.Rows[i];
            Set(player, $"{id}_k", row.Label);
            Cls(player, id, "hidden", false);

            switch (row)
            {
                case OptionsRow options:
                    Cls(player, id, "kind-go", false);
                    for (var j = 0; j < OptionSlots; j++)
                    {
                        var optionId = $"{id}_o{j}";
                        if (j >= options.Options.Count)
                        {
                            Cls(player, optionId, "hidden", true);
                            continue;
                        }

                        var option = options.Options[j];
                        Set(player, $"{optionId}_t", option.Label);
                        Cls(player, optionId, "sel", option.Selected);
                        Cls(player, optionId, "locked", option.Locked);
                        Cls(player, optionId, "hidden", false);
                    }

                    break;

                case OpenRow:
                    Cls(player, id, "kind-go", true);
                    Set(player, $"{id}_go_t", _hud.T(player, "hud.menu.open", "OPEN") + " ›");
                    break;

                case LinkRow link:
                    Cls(player, id, "kind-go", true);
                    Set(player, $"{id}_go_t", link.Link.Text + " ›");
                    break;
            }
        }
    }

    private List<Row> BuildRows(CCSPlayerController player, VipPlayer vip)
    {
        var rows = new List<Row>();

        var features = _hud.Features.GetFeatures()
            .Where(f => f.Type is not FeatureType.Hide && vip.HasFeature(f.Name))
            .OrderBy(f => Array.IndexOf(_hud.SortOrder, f.Name))
            .ThenBy(f => f.Name)
            .ToList();

        foreach (var feature in features)
        {
            var state = vip.FeatureStates.GetValueOrDefault(feature.Name, FeatureState.NoAccess);
            if (state is FeatureState.NoAccess) continue;

            var label = _hud.Plugin.Translate(player, feature.Name);
            var options = Options(player, feature);

            if (options is { Count: > 0 })
                rows.Add(new OptionsRow(label, feature, options.Take(OptionSlots).ToList(), false));
            else if (feature.Type is FeatureType.Toggle)
                rows.Add(new OptionsRow(label, feature,
                [
                    new FeatureOption("on", _hud.T(player, "hud.on", "ON"), state is FeatureState.Enabled),
                    new FeatureOption("off", _hud.T(player, "hud.off", "OFF"), state is FeatureState.Disabled),
                ], true));
            else
                rows.Add(new OpenRow(label, feature));
        }

        foreach (var link in _hud.Config.Links)
        {
            if (string.IsNullOrEmpty(link.Feature) || vip.HasFeature(link.Feature))
                rows.Add(new LinkRow(link.Label, link));
        }

        return rows.Take(RowSlots).ToList();
    }

    private IReadOnlyList<FeatureOption>? Options(CCSPlayerController player, VipFeature feature)
    {
        try
        {
            return feature.GetMenuOptions(player);
        }
        catch (Exception e)
        {
            _hud.Plugin.Logger.LogError("Exception in GetMenuOptions of '{feature}': {e}", feature.Name, e);
            return null;
        }
    }

    private void DrawCompare(CCSPlayerController player)
    {
        var config = _hud.Config;
        var all = _hud.Groups.Value;

        Set(player, "vc_intro", _hud.T(player, "hud.menu.compare_intro", "You don't have VIP yet. What each group gets:"));
        Set(player, "vc_cta", _hud.T(player, "hud.menu.cta", "Get VIP on {0} - it starts mid-game, no reconnect.",
            config.Website));

        var groups = (config.CompareGroups.Length > 0 ? config.CompareGroups.Where(all.ContainsKey) : all.Keys)
            .Take(CompareColumns)
            .ToList();
        var resolved = groups.Select(Resolve).ToList();

        for (var j = 0; j < CompareColumns; j++)
        {
            var id = $"vc_h{j}";
            if (j >= groups.Count)
            {
                Cls(player, id, "hidden", true);
                continue;
            }

            Set(player, id, groups[j]);
            _classes.Set(_panel, player, id, "g-" + HudFormat.Style(config, groups[j]), HudFormat.StyleClasses);
            Cls(player, id, "hidden", false);
        }

        var perks = config.Perks.Take(CompareRows).ToList();
        for (var i = 0; i < CompareRows; i++)
        {
            var id = $"vc_r{i}";
            if (i >= perks.Count)
            {
                Cls(player, id, "hidden", true);
                continue;
            }

            var perk = perks[i];
            Set(player, $"{id}_k", perk.Label);
            Cls(player, id, "hidden", false);

            for (var j = 0; j < CompareColumns; j++)
            {
                var cell = $"{id}_c{j}";
                if (j >= groups.Count)
                {
                    Cls(player, cell, "hidden", true);
                    continue;
                }

                var value = HudFormat.Read(resolved[j], perk);
                Set(player, cell,
                    !value.On ? "-" : HudFormat.IsFlag(perk) ? _hud.T(player, "hud.yes", "Yes") : value.Text);
                Cls(player, cell, "no", !value.On);
                Cls(player, cell, "hidden", false);
            }
        }
    }

    private Dictionary<string, object>? Resolve(string group)
    {
        try
        {
            return _hud.Groups.Value.ResolveGroup(group);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ input

    private void OnEvent(PanelEvent e)
    {
        if (e.Player is not { IsValid: true } player) return;

        switch (e.Action)
        {
            case PanelAction.Close:
                _classes.Forget(player.Slot);
                _viewers.Remove(player.Slot);
                return;

            case PanelAction.Restored:
                if (!_viewers.TryGetValue(player.Slot, out var restored))
                {
                    _panel.Close(player);
                    return;
                }

                _classes.Forget(player.Slot);
                Draw(player, restored);
                return;

            case PanelAction.Button:
                if (_viewers.TryGetValue(player.Slot, out var viewer))
                    Click(player, viewer, e.ElementId);
                return;
        }
    }

    /// <summary>vm_rN_oM (an option) or vm_rN_go (a go button).</summary>
    private void Click(CCSPlayerController player, Viewer viewer, string id)
    {
        if (!id.StartsWith("vm_r")) return;

        var rest = id[4..];
        var split = rest.IndexOf('_');
        if (split < 1 || !int.TryParse(rest[..split], out var index) || index < 0 || index >= viewer.Rows.Count) return;
        var part = rest[(split + 1)..];

        // VIP gone or changed since the draw: show what is true now instead
        var vip = _hud.Member(player);
        if (vip == null)
        {
            Draw(player, viewer);
            return;
        }

        switch (viewer.Rows[index])
        {
            case OptionsRow row when part.StartsWith('o') && int.TryParse(part[1..], out var o) && o >= 0 &&
                                     o < row.Options.Count:
            {
                var option = row.Options[o];
                if (option.Locked || option.Selected || !Usable(vip, row.Feature)) return;

                if (row.Toggle)
                    SetToggle(player, vip, row.Feature, option.Value == "on");
                else
                    SelectOption(player, vip, row.Feature, option.Value);

                Draw(player, viewer);
                return;
            }

            case OpenRow row when part == "go":
            {
                if (!Usable(vip, row.Feature)) return;

                _panel.Close(player);
                var feature = row.Feature;
                AfterClose(player, again => OpenFeatureMenu(again, feature));
                return;
            }

            case LinkRow row when part == "go":
            {
                var command = row.Link.Command.Trim();
                if (command.Length == 0) return;

                _panel.Close(player);
                AfterClose(player, again => again.ExecuteClientCommandFromServer(command));
                return;
            }
        }
    }

    /// <summary>Still registered (the module may have unloaded since the draw) and still in the player's group.</summary>
    private bool Usable(VipPlayer vip, VipFeature feature) =>
        _hud.Features.FindByName(feature.Name) == feature && vip.HasFeature(feature.Name) &&
        vip.FeatureStates.GetValueOrDefault(feature.Name, FeatureState.NoAccess) is not FeatureState.NoAccess;

    /// <summary>The old menu's toggle (MenuManager.OnFeatureSelected), without its chat line: the row shows the state.</summary>
    private void SetToggle(CCSPlayerController player, VipPlayer vip, VipFeature feature, bool on)
    {
        var state = vip.FeatureStates.GetValueOrDefault(feature.Name, FeatureState.NoAccess);
        var wanted = on ? FeatureState.Enabled : FeatureState.Disabled;
        if (state is FeatureState.NoAccess || state == wanted) return;

        if (!Allowed(player, feature, state)) return;

        vip.FeatureStates[feature.Name] = wanted;
        _hud.Cookies.Set(player.SteamID, feature.Name, (int)wanted);

        try
        {
            feature.OnSelectItem(player, feature);
        }
        catch (Exception e)
        {
            _hud.Plugin.Logger.LogError("Exception in OnSelectItem of '{feature}': {e}", feature.Name, e);
        }
    }

    private void SelectOption(CCSPlayerController player, VipPlayer vip, VipFeature feature, string value)
    {
        // Offered unlocked right now, not only when the menu was drawn
        var option = Options(player, feature)?.FirstOrDefault(o => o.Value == value);
        if (option is null || option.Locked) return;

        if (!Allowed(player, feature, vip.FeatureStates.GetValueOrDefault(feature.Name, FeatureState.NoAccess))) return;

        try
        {
            feature.OnMenuOptionSelected(player, value);
        }
        catch (Exception e)
        {
            _hud.Plugin.Logger.LogError("Exception in OnMenuOptionSelected of '{feature}': {e}", feature.Name, e);
        }
    }

    private void OpenFeatureMenu(CCSPlayerController player, VipFeature feature)
    {
        if (_hud.Member(player) is not { } vip || !Usable(vip, feature)) return;
        if (!Allowed(player, feature, vip.FeatureStates.GetValueOrDefault(feature.Name, FeatureState.NoAccess))) return;

        try
        {
            feature.OnSelectItem(player, feature);
        }
        catch (Exception e)
        {
            _hud.Plugin.Logger.LogError("Exception in OnSelectItem of '{feature}': {e}", feature.Name, e);
        }
    }

    /// <summary>OnPlayerUseFeature, which modules can veto - the same gate the old menu runs.</summary>
    private bool Allowed(CCSPlayerController player, VipFeature feature, FeatureState state)
    {
        var args = new PlayerUseFeatureEventArgs { Controller = player, Feature = feature, State = state };
        _hud.Api.InvokeOnPlayerUseFeature(args);
        return args.Allow;
    }

    /// <summary>Runs <paramref name="action"/> next frame, once this menu has let go of the mouse.</summary>
    private static void AfterClose(CCSPlayerController player, Action<CCSPlayerController> action)
    {
        var slot = player.Slot;
        var steamId = player.SteamID;
        Server.NextFrame(() =>
        {
            var again = Utilities.GetPlayerFromSlot(slot);
            if (again is { IsValid: true } && again.SteamID == steamId) action(again);
        });
    }

    private void Set(CCSPlayerController player, string name, string value) =>
        _panel.SetVariableFor(player, name, value);

    private void Cls(CCSPlayerController player, string id, string cls, bool on) =>
        _panel.SetClassFor(player, id, cls, on);
}
