namespace VIPCore.Configs;

/// <summary>
/// Next-il: the "Hud" section of vip_core.json - the welcome/status card and the !vip mouse menu (Hud/). Every key
/// has a default, so a vip_core.json without the section loads unchanged; the file is shared by every server on a
/// node and is never written back.
/// </summary>
public class HudConfig
{
    /// <summary>Off: no HUD at all - the old chat lines and the old center menu, exactly as before. Needs a map change.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long the welcome/status card stays up.</summary>
    public float CardSeconds { get; init; } = 7f;

    /// <summary>At this many days left or fewer the card goes red and asks to renew.</summary>
    public int SoonDays { get; init; } = 3;

    /// <summary>The days-left bar is full at this many days.</summary>
    public int BarDays { get; init; } = 30;

    /// <summary>Where VIP is bought, in the renew line and under the group comparison.</summary>
    public string Website { get; init; } = "next-il.co.il";

    /// <summary>IANA id. "until 20 Dec" is the date in this zone, not the server's.</summary>
    public string TimeZone { get; init; } = "Asia/Jerusalem";

    /// <summary>A player who leaves and is back within this many seconds is on the same visit: no second welcome.</summary>
    public int RejoinGraceSeconds { get; init; } = 300;

    /// <summary>Show players without VIP a card at first spawn too, pointing at !vip. Off by default.</summary>
    public bool WelcomeNonVips { get; init; } = false;

    /// <summary>
    /// vip.json group -> palette: "vip" (blue), "premium" (purple), "supreme" (orange) or "none" (grey). Panorama
    /// cannot take a color from the server, so these four are baked into the layout. A group not listed is grey.
    /// Matched without case.
    /// </summary>
    public Dictionary<string, string> GroupStyles { get; init; } = new()
    {
        ["VIP"] = "vip",
        ["PREMIUM"] = "premium",
        ["SUPREME"] = "supreme",
    };

    /// <summary>The groups in the comparison non-VIPs see on !vip, in order (4 at most). Empty: vip.json's, in file order.</summary>
    public string[] CompareGroups { get; init; } = [];

    /// <summary>
    /// What a group gets, read from vip.json by key. The same list feeds the card's perks line (Short), the menu's
    /// tiles (Tile, 4 at most) and the comparison table (every entry, 8 at most).
    /// </summary>
    public List<HudPerk> Perks { get; init; } =
    [
        new() { Key = "exp_multiplier", Label = "XP bonus", Short = "XP {0}", Kind = "multiplier", Tile = true },
        new() { Key = "Store", Label = "Credits bonus", Short = "CREDITS {0}", Kind = "multiplier", Tile = true },
        new() { Key = "Fov", Label = "FOV up to", Short = "FOV {0}", Kind = "max" },
        new() { Key = "MusicKitLimit", Label = "Music kits", Short = "{0} MUSIC KITS", Kind = "number", Tile = true },
        new() { Key = "Skybox", Label = "Skybox", Short = "SKYBOX", Kind = "flag", Tile = true },
        // The chat tag: vip.json ScoreboardTag is empty since 2026-11-22 (owner: no VIP clan tag, the scoreboard shows the rank).
        new() { Key = "ChatTag", Label = "Chat tag", Short = "", Kind = "text" },
    ];

    /// <summary>
    /// Menu rows that open another plugin's menu. Shown when the player's group has Feature (empty: always); a
    /// click closes !vip and runs Command as the player.
    /// </summary>
    public List<HudLink> Links { get; init; } =
    [
        new() { Label = "Music kit", Text = "!music", Command = "css_music", Feature = "MusicKitLimit" },
        new() { Label = "Skybox", Text = "!sky", Command = "css_sky", Feature = "Skybox" },
    ];
}

public class HudPerk
{
    /// <summary>The vip.json key.</summary>
    public string Key { get; init; } = "";

    /// <summary>Tile and comparison label.</summary>
    public string Label { get; init; } = "";

    /// <summary>On the card's perks line, {0} = the value. Empty: not on the card.</summary>
    public string Short { get; init; } = "";

    /// <summary>"multiplier" (x1.5), "number" (35), "max" (the highest of a list: FOV 120), "flag" (on/off) or "text".</summary>
    public string Kind { get; init; } = "number";

    /// <summary>One of the menu's four tiles.</summary>
    public bool Tile { get; init; }
}

public class HudLink
{
    public string Label { get; init; } = "";

    /// <summary>The button text; " ›" is added.</summary>
    public string Text { get; init; } = "";

    /// <summary>Run as the player on click. Empty: the row is only a hint.</summary>
    public string Command { get; init; } = "";

    /// <summary>The vip.json key the player's group needs for the row to show. Empty: always shown.</summary>
    public string Feature { get; init; } = "";
}
