namespace Cms.Client;

/// <summary>
/// Ctrl+Shift+U, the staff unlock, while the PC is locked (Windows test 2026-11-27 night, defect 3: on the paused screen
/// the first Ctrl+Shift+U was lost; defect 2: it did nothing while another window had the foreground). The low-level
/// keyboard hook of the lock screen sees every key, so it starts the PIN prompt whatever window has the keyboard. One
/// prompt per press: the key repeat of a held U does not start it again.
/// </summary>
public sealed class StaffChord
{
    public const int VkU = 0x55;
    private bool _held;

    /// <summary>The hook is installed again (a new lock): a key-up that the hook did not see does not lose the next press.</summary>
    public void Reset() => _held = false;

    /// <summary>A key event in the hook. True: start the PIN prompt now. Swallow: the key does not go on to a window.</summary>
    public (bool Fire, bool Swallow) OnKey(int vk, bool down, bool ctrl, bool shift, bool alt, bool win)
    {
        if (vk != VkU) return (false, false);
        if (!down)
        {
            _held = false;
            return (false, false);
        }
        if (!ctrl || !shift || alt || win) return (false, false);
        var fire = !_held;
        _held = true;
        return (fire, true);
    }
}
