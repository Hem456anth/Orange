using Orange.Core.Input;
using Xunit;

namespace Orange.Core.Tests;

public class WheelKeyTests
{
    private static WheelTarget? Down(WheelKeyInterpreter k, ModifierKey key, long t, bool shift = false, bool ctrl = false, bool alt = false)
        => k.OnKeyDown(key, shift || key == ModifierKey.Shift, ctrl || key == ModifierKey.Ctrl, alt || key == ModifierKey.Alt, t);

    [Fact]
    public void Defaults_are_shift_double_shift_and_shift_ctrl()
    {
        var k = new WheelKeyInterpreter();
        Assert.Equal(WheelKey.Shift, k.ConvertKey);
        Assert.Equal(WheelKey.ShiftDouble, k.ToolsKey);
        Assert.Equal(WheelKey.ShiftCtrl, k.ActionsKey);
    }

    [Fact]
    public void Single_tap_is_convert_and_quick_second_tap_is_tools()
    {
        var k = new WheelKeyInterpreter();
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 1000));
        Assert.Equal(WheelTarget.Tools, Down(k, ModifierKey.Shift, 1300));
        // A third tap starts a new sequence.
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 1500));
    }

    [Fact]
    public void Slow_second_tap_stays_convert()
    {
        var k = new WheelKeyInterpreter();
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 1000));
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 2000));
    }

    [Fact]
    public void Shift_and_ctrl_in_either_order_is_actions()
    {
        var k = new WheelKeyInterpreter();
        Assert.Equal(WheelTarget.Actions, Down(k, ModifierKey.Ctrl, 10, shift: true));
        Assert.Equal(WheelTarget.Actions, Down(k, ModifierKey.Shift, 20, ctrl: true));
    }

    [Fact]
    public void Plain_ctrl_and_alt_do_nothing_by_default()
    {
        // Explorer uses them for copy/link; by default Orange leaves them alone.
        var k = new WheelKeyInterpreter();
        Assert.Null(Down(k, ModifierKey.Ctrl, 10));
        Assert.Null(Down(k, ModifierKey.Alt, 20));
    }

    [Fact]
    public void Chord_does_not_arm_a_double_tap()
    {
        var k = new WheelKeyInterpreter();
        Assert.Equal(WheelTarget.Actions, Down(k, ModifierKey.Shift, 100, ctrl: true));
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 200));
    }

    [Fact]
    public void Old_scheme_still_works_when_configured()
    {
        var k = new WheelKeyInterpreter { ConvertKey = WheelKey.Shift, ToolsKey = WheelKey.AltShift, ActionsKey = WheelKey.Ctrl };
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 0));
        Assert.Equal(WheelTarget.Convert, Down(k, ModifierKey.Shift, 100));   // no double-tap binding → plain Shift
        Assert.Equal(WheelTarget.Tools, Down(k, ModifierKey.Shift, 300, alt: true));
        Assert.Equal(WheelTarget.Actions, Down(k, ModifierKey.Ctrl, 400));
        // Shift+Ctrl unbound → falls back to the key just pressed (Ctrl = Actions).
        Assert.Equal(WheelTarget.Actions, Down(k, ModifierKey.Ctrl, 500, shift: true));
    }

    [Fact]
    public void Held_keys_at_drag_start()
    {
        var k = new WheelKeyInterpreter();
        Assert.Equal(WheelTarget.Convert, k.ForHeld(true, false, false));
        Assert.Equal(WheelTarget.Actions, k.ForHeld(true, true, false));
        Assert.Null(k.ForHeld(false, true, false));
        Assert.Null(k.ForHeld(false, false, false));
    }

    [Theory]
    [InlineData(WheelKey.ShiftDouble, "Shift twice")]
    [InlineData(WheelKey.ShiftCtrl, "Shift+Ctrl")]
    [InlineData(WheelKey.AltShift, "Alt+Shift")]
    [InlineData(WheelKey.Ctrl, "Ctrl")]
    public void Labels(WheelKey key, string label) => Assert.Equal(label, WheelKeyInterpreter.Label(key));
}
