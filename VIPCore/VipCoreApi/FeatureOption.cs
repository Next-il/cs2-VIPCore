namespace VipCoreApi;

/// <summary>
/// Next-il: one choice a feature offers in the !vip HUD menu - one button in the feature's row. See
/// <see cref="VipFeature.GetMenuOptions"/>.
/// </summary>
/// <param name="Value">What comes back to <see cref="VipFeature.OnMenuOptionSelected"/> when it is clicked.</param>
/// <param name="Label">The button text. Keep it short: a row has room for about eight 3-character buttons.</param>
/// <param name="Selected">The player's current choice.</param>
/// <param name="Locked">Shown with a lock and not clickable - a choice another VIP group has, as a hint.</param>
public sealed record FeatureOption(string Value, string Label, bool Selected, bool Locked = false);
