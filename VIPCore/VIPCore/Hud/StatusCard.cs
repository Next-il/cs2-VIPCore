using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using PanoramaManager;

namespace VIPCore.Hud;

/// <summary>What one card shows. Built by <see cref="VipHud"/>, drawn by <see cref="StatusCard"/>.</summary>
/// <param name="State">welcome, new, up or gone - the st- class.</param>
/// <param name="Style">The palette: vip, premium, supreme or none.</param>
/// <param name="Soon">3 days or less left: red days left and the renew line.</param>
/// <param name="Perm">Permanent: no bar and no date.</param>
/// <param name="Old">The group before an upgrade, struck through. Shown for State "up" only.</param>
/// <param name="Step">The bar's class, p0..p20.</param>
/// <param name="Pop">Pop the star once the card is in.</param>
internal sealed record CardData(
    string State,
    string Style,
    bool Soon,
    bool Perm,
    string Eyebrow,
    string Old,
    string Group,
    string Title,
    string Sub,
    IReadOnlyList<string> Perks,
    string Left,
    string Until,
    string Step,
    bool Pop);

/// <summary>
/// The welcome/status card (vip_hud.xml): bottom center, for <see cref="Configs.HudConfig.CardSeconds"/>. Passive - it
/// never takes the mouse.
///
/// <para><b>One layout, opened once per player and left open.</b> The root is an invisible full-screen panel and the
/// card inside carries its own "show", so showing and hiding a card is a class toggle, never an Open/Close - the same
/// as the Retakes HUD.</para>
///
/// <para><b>Everything is redrawn from state.</b> The library rebuilds a player's panel by itself after a round restart
/// or a map change and drops every per-viewer variable and class on the way (PanelAction.Restored), so each viewer's
/// card is kept here and written back. A card whose time is up stays hidden.</para>
/// </summary>
internal sealed class StatusCard : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/vip_hud.vxml_c";
    private const string Root = "VipHudRoot";
    private const string Card = "vw";
    private const int PerkSlots = 6;

    private static readonly string[] States = ["st-welcome", "st-new", "st-up", "st-gone"];

    private sealed class Viewer
    {
        public CardData? Card;
        public DateTime Until;
        public bool Shown;
        public bool PopA;
    }

    private readonly VipHud _hud;
    private readonly PanelHandle _panel;
    private readonly ClassTracker _classes = new();
    private readonly Dictionary<int, Viewer> _viewers = [];

    public StatusCard(VipHud hud)
    {
        _hud = hud;
        _panel = Panorama.Spawn(Layout, new LayoutContract
        {
            // Unique server-wide: dialog variables are keyed by panel id
            RootPanelId = Root,
            RevealClass = "show",
            RowCount = 0,
            CaptureInput = false,
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

    /// <summary>Shows <paramref name="card"/> for <paramref name="seconds"/>, replacing whatever card is up.</summary>
    public void Show(CCSPlayerController player, CardData card, float seconds)
    {
        if (!IsHuman(player)) return;

        var fresh = !_panel.IsOpenFor(player);
        if (fresh)
        {
            _panel.Open(player);
            if (!_panel.IsOpenFor(player)) return;
            _classes.Forget(player.Slot);
        }

        var viewer = ViewerOf(player.Slot);
        viewer.Card = card;
        viewer.Until = DateTime.UtcNow.AddSeconds(seconds);
        viewer.Shown = true;

        var slot = player.Slot;
        if (fresh)
        {
            // The root only just became visible: lay the card out hidden first, so its slide-in has a start to play from
            Apply(player, viewer, false);
            _hud.Plugin.AddTimer(0.1f, () =>
            {
                if (Current(slot, card) is { } again) Apply(again, viewer, true);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
        else
        {
            Apply(player, viewer, true);
        }

        if (card.Pop)
        {
            _hud.Plugin.AddTimer(0.32f, () =>
            {
                if (Current(slot, card) is not { } again) return;

                // Ping-pong: the only restart a class diff can carry
                viewer.PopA = !viewer.PopA;
                _panel.SetClassFor(again, "vw_crest", "pop-a", viewer.PopA);
                _panel.SetClassFor(again, "vw_crest", "pop-b", !viewer.PopA);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    /// <summary>Takes down cards whose time is up. Runs on a short repeating timer that survives map changes.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var (slot, viewer) in _viewers)
        {
            if (!viewer.Shown || now < viewer.Until) continue;

            viewer.Shown = false;
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true } && _panel.IsOpenFor(player))
                _panel.SetClassFor(player, Card, "show", false);
        }
    }

    public void Forget(int slot)
    {
        _viewers.Remove(slot);
        _classes.Forget(slot);
        _panel.Close(slot);
    }

    private void OnEvent(PanelEvent e)
    {
        if (e.Player is not { IsValid: true } player) return;

        switch (e.Action)
        {
            case PanelAction.Restored:
                _classes.Forget(player.Slot);
                if (_viewers.TryGetValue(player.Slot, out var viewer))
                    Apply(player, viewer, viewer.Shown && DateTime.UtcNow < viewer.Until);
                return;

            case PanelAction.Close:
                _classes.Forget(player.Slot);
                return;
        }
    }

    private void Apply(CCSPlayerController player, Viewer viewer, bool show)
    {
        if (!_panel.IsOpenFor(player)) return;

        if (viewer.Card is { } card)
        {
            Set(player, "vw_eyebrow", card.Eyebrow);
            Set(player, "vw_old", card.Old);
            Set(player, "vw_group", card.Group);
            Set(player, "vw_title", card.Title);
            Set(player, "vw_sub", card.Sub);
            Set(player, "vw_left", card.Left);
            Set(player, "vw_until", card.Until);

            for (var i = 0; i < PerkSlots; i++)
            {
                var on = i < card.Perks.Count;
                Set(player, $"vw_p{i}", on ? card.Perks[i] : "");
                _panel.SetClassFor(player, $"vw_p{i}", "hidden", !on);
            }

            _classes.Set(_panel, player, Card, "g-" + card.Style, HudFormat.StyleClasses);
            _classes.Set(_panel, player, Card, "st-" + card.State, States);
            _panel.SetClassFor(player, Card, "soon", card.Soon);
            _panel.SetClassFor(player, Card, "perm", card.Perm);
            _classes.Set(_panel, player, "vw_fill", card.Step, HudFormat.StepClasses);
        }

        _panel.SetClassFor(player, Card, "show", show && viewer.Card != null);
    }

    private void Set(CCSPlayerController player, string name, string value) =>
        _panel.SetVariableFor(player, name, value);

    private Viewer ViewerOf(int slot)
    {
        if (!_viewers.TryGetValue(slot, out var viewer))
        {
            viewer = new Viewer();
            _viewers[slot] = viewer;
        }

        return viewer;
    }

    /// <summary>The player in <paramref name="slot"/>, if <paramref name="card"/> is still their card and the panel is open.</summary>
    private CCSPlayerController? Current(int slot, CardData card)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        return player is { IsValid: true } && _viewers.TryGetValue(slot, out var viewer) && viewer.Card == card &&
               viewer.Shown && _panel.IsOpenFor(player)
            ? player
            : null;
    }

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;
}
