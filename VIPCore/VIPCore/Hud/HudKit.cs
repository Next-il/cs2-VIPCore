using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;
using PanoramaManager;
using VIPCore.Configs;

namespace VIPCore.Hud;

/// <summary>
/// One-of-many classes per viewer: a palette (g-vip..g-none), a bar step (p0..p20), a card state. If two are on at
/// once the stylesheet paints whichever it defines last, so the old one has to come off before the new one goes on,
/// which means knowing which one is on. The Ranks HUD's, keyed by the option list too, so one panel can carry
/// several groups (the card has a palette and a state).
///
/// <para>Forget a viewer whenever their session is reset: <see cref="PanelHandle.Open"/> and a rebuilt entity take
/// every per-viewer class back off, and a stale entry here would then skip writing a class that is no longer there.</para>
/// </summary>
internal sealed class ClassTracker
{
    private readonly Dictionary<(int Slot, string Panel, IReadOnlyList<string> Group), string?> _current = new();

    /// <summary>
    /// Puts <paramref name="className"/> on (null: none of them) and takes the other options off. While the tracker
    /// knows what is on, only the difference is written; after <see cref="Forget"/> every option is written.
    /// </summary>
    public void Set(PanelHandle panel, CCSPlayerController player, string panelId, string? className,
        IReadOnlyList<string> options)
    {
        var key = (player.Slot, panelId, options);
        if (_current.TryGetValue(key, out var old))
        {
            if (old == className) return;
            if (old != null) panel.SetClassFor(player, panelId, old, false);
        }
        else
        {
            foreach (var option in options)
                if (option != className) panel.SetClassFor(player, panelId, option, false);
        }

        if (className != null) panel.SetClassFor(player, panelId, className, true);
        _current[key] = className;
    }

    public void Forget(int slot)
    {
        foreach (var key in _current.Keys.Where(k => k.Slot == slot).ToList())
            _current.Remove(key);
    }
}

/// <summary>A perk as one group has it: on or not, and its value as text.</summary>
internal readonly record struct PerkValue(bool On, string Text);

internal static partial class HudFormat
{
    public const int Steps = 20;

    /// <summary>The bar ladder both layouts carry: p0..p20, 5% a step.</summary>
    public static readonly string[] StepClasses = Enumerable.Range(0, Steps + 1).Select(i => "p" + i).ToArray();

    /// <summary>The four palettes baked into the layouts.</summary>
    public static readonly string[] Styles = ["vip", "premium", "supreme", "none"];

    public static readonly string[] StyleClasses = Styles.Select(s => "g-" + s).ToArray();

    public static string Step(double fraction) =>
        "p" + Math.Clamp((int)Math.Round(Math.Clamp(fraction, 0, 1) * Steps), 0, Steps);

    public static string Style(HudConfig config, string group)
    {
        foreach (var (name, style) in config.GroupStyles)
        {
            if (!name.Equals(group, StringComparison.OrdinalIgnoreCase)) continue;

            var s = style.Trim().ToLowerInvariant();
            return Styles.Contains(s) ? s : "none";
        }

        return "none";
    }

    /// <summary>"x1.3", "x2".</summary>
    public static string Multiplier(double value) => "x" + value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads one perk out of a resolved vip.json group (the player's own <c>VipPlayer.Features</c>, or any group's).
    /// Missing or zero is off. A x1 multiplier is off too: it adds nothing.
    /// </summary>
    public static PerkValue Read(Dictionary<string, object>? features, HudPerk perk)
    {
        if (features == null || !features.TryGetValue(perk.Key, out var raw) || raw is not JsonElement value)
            return new PerkValue(false, "");

        try
        {
            switch (perk.Kind.ToLowerInvariant())
            {
                case "multiplier":
                {
                    var n = Number(value);
                    return n is > 1 ? new PerkValue(true, Multiplier(n.Value)) : new PerkValue(false, "");
                }
                case "max":
                {
                    var max = value.ValueKind == JsonValueKind.Array
                        ? value.EnumerateArray().Select(Number).Where(n => n is > 0).Max()
                        : Number(value);
                    return max is > 0
                        ? new PerkValue(true, max.Value.ToString("0.##", CultureInfo.InvariantCulture))
                        : new PerkValue(false, "");
                }
                case "flag":
                    return new PerkValue(value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.Number => value.GetDouble() != 0,
                        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
                        JsonValueKind.Array => value.GetArrayLength() > 0,
                        _ => false,
                    }, "");
                case "text":
                {
                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                    // A chat tag carries {Color} codes; the HUD shows the words only
                    text = text == null ? null : ColorCode().Replace(text, "").Trim();
                    return string.IsNullOrEmpty(text) ? new PerkValue(false, "") : new PerkValue(true, text);
                }
                default:
                {
                    var n = Number(value);
                    return n is > 0
                        ? new PerkValue(true, n.Value.ToString("0.##", CultureInfo.InvariantCulture))
                        : new PerkValue(false, "");
                }
            }
        }
        catch (Exception)
        {
            return new PerkValue(false, "");
        }
    }

    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => null,
    };

    public static bool IsFlag(HudPerk perk) => perk.Kind.Equals("flag", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex ColorCode();
}
