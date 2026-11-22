namespace VIPCore.Player;

/// <summary>Next-il: a VIP status change the player is told about - on the HUD card, or as the old chat line.</summary>
public enum NoticeKind
{
    /// <summary>Loaded as VIP on connect (and again after every map change).</summary>
    Welcome,

    /// <summary>Loaded without VIP on connect.</summary>
    NotVip,

    /// <summary>Got VIP while in game: bought on the website, or given by an admin.</summary>
    Activated,

    GroupChanged,
    TimeChanged,
    Expired,
    Removed,
}

/// <param name="Player">The player, already in the new state.</param>
/// <param name="Group">The group now, or the one that was lost for Expired/Removed.</param>
/// <param name="OldGroup">GroupChanged: the group before.</param>
/// <param name="OldExpires">GroupChanged/TimeChanged: the expiry before (0 = permanent).</param>
public sealed record VipNotice(VipPlayer Player, NoticeKind Kind, string Group, string? OldGroup = null, long OldExpires = 0);
