using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Windows test 2026-11-27 night, defects 2 and 3: Ctrl+Shift+U was lost on the paused screen and did nothing while another window had the keyboard.</summary>
public sealed class StaffChordTests
{
    [Fact]
    public void Ctrl_shift_u_starts_one_prompt_per_press_whatever_has_the_keyboard()
    {
        var chord = new StaffChord();
        Assert.Equal((true, true), chord.OnKey(StaffChord.VkU, down: true, ctrl: true, shift: true, alt: false, win: false));
        Assert.Equal((false, true), chord.OnKey(StaffChord.VkU, true, true, true, false, false));   // key repeat: no second prompt
        Assert.Equal((false, false), chord.OnKey(StaffChord.VkU, false, true, true, false, false)); // key up
        Assert.Equal((true, true), chord.OnKey(StaffChord.VkU, true, true, true, false, false));    // the next press
        chord.Reset();                                                                            // the hook missed the key-up (unlocked)
        Assert.Equal((true, true), chord.OnKey(StaffChord.VkU, true, true, true, false, false));
    }

    [Fact]
    public void Other_keys_and_combinations_go_on_to_the_window()
    {
        var chord = new StaffChord();
        Assert.Equal((false, false), chord.OnKey(StaffChord.VkU, true, ctrl: true, shift: false, alt: false, win: false));
        Assert.Equal((false, false), chord.OnKey(StaffChord.VkU, true, ctrl: false, shift: true, alt: false, win: false));
        Assert.Equal((false, false), chord.OnKey(StaffChord.VkU, true, true, true, alt: true, win: false));
        Assert.Equal((false, false), chord.OnKey(StaffChord.VkU, true, true, true, false, win: true));
        Assert.Equal((false, false), chord.OnKey(0x41, true, true, true, false, false));             // Ctrl+Shift+A
    }
}
