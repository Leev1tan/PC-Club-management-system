namespace Cms.Client;

/// <summary>
/// What the lock screen does with the club logo after it read client-settings.json and club-logo.img
/// (Cms.Launcher/MainWindow.Theme.cs). Keep: nothing changes (the agent may still download a new logo, so the old one
/// stays). ShowImage: the new logo. ShowText: the club name text, when the club has no logo any more, or when the file has
/// the right hash but Windows cannot decode it (then the replaced logo must not stay on the screen).
/// </summary>
public static class LogoDisplay
{
    public enum Action { Keep, ShowImage, ShowText }

    /// <param name="wanted">Logo hash of the settings (null: no logo).</param>
    /// <param name="shown">Hash of the logo or text state on the screen now (null: the text without a logo).</param>
    /// <param name="trustedBytes">club-logo.img has the wanted hash (Cms.Shared.LogoLoader).</param>
    /// <param name="decoded">Windows decoded those bytes.</param>
    public static Action Decide(string? wanted, string? shown, bool trustedBytes, bool decoded)
    {
        if (wanted == shown) return Action.Keep;
        if (wanted == null) return Action.ShowText;
        if (!trustedBytes) return Action.Keep;
        return decoded ? Action.ShowImage : Action.ShowText;
    }
}
