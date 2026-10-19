namespace Orange.Core.Input;

/// <summary>
/// A key (or key gesture) that opens a wheel while dragging. Stored by name in settings.json, so names are stable.
/// ShiftDouble = Shift tapped twice quickly; ShiftCtrl and AltShift = the two keys together, in either order.
/// </summary>
public enum WheelKey { Shift, Ctrl, Alt, AltShift, ShiftDouble, ShiftCtrl }

public enum WheelTarget { Convert, Tools, Actions }

public enum ModifierKey { Shift, Ctrl, Alt }

/// <summary>
/// Turns modifier key presses during a drag into "open / switch to this wheel". Pure logic (no Windows calls) so it
/// is unit-tested; the app's low-level keyboard hook feeds it.
///
/// Defaults (0.2): Shift = Convert, Shift twice = Tools, Shift+Ctrl = Actions. Explorer reads Shift/Ctrl/Alt during a
/// drag as move/copy/link; Orange's drop target always answers Copy, so none of these can move the source.
/// </summary>
public sealed class WheelKeyInterpreter
{
    public WheelKey ConvertKey { get; set; } = WheelKey.Shift;
    public WheelKey ToolsKey { get; set; } = WheelKey.ShiftDouble;
    public WheelKey ActionsKey { get; set; } = WheelKey.ShiftCtrl;
    public int DoubleTapMs { get; set; } = 450;

    private long _lastShiftTap = long.MinValue / 2;

    /// <summary>Forget a pending first tap (call when a drag ends).</summary>
    public void Reset() => _lastShiftTap = long.MinValue / 2;

    /// <summary>
    /// A modifier went down (not an auto-repeat). <paramref name="shift"/>/<paramref name="ctrl"/>/<paramref name="alt"/>
    /// say which modifiers are held, including the one just pressed. Returns the wheel to show, or null.
    /// </summary>
    public WheelTarget? OnKeyDown(ModifierKey key, bool shift, bool ctrl, bool alt, long nowMs)
    {
        var gesture = Gesture(key, shift, ctrl, alt, nowMs);
        return gesture is null ? null : Resolve(gesture.Value, key);
    }

    /// <summary>Keys already held when a drag starts ("hold Shift and drag").</summary>
    public WheelTarget? ForHeld(bool shift, bool ctrl, bool alt)
    {
        if (shift && ctrl) return Resolve(WheelKey.ShiftCtrl, ModifierKey.Ctrl);
        if (shift && alt) return Resolve(WheelKey.AltShift, ModifierKey.Shift);
        if (shift) return Target(WheelKey.Shift);
        if (ctrl) return Target(WheelKey.Ctrl);
        if (alt) return Target(WheelKey.Alt);
        return null;
    }

    public WheelTarget? Target(WheelKey k) =>
        k == ConvertKey ? WheelTarget.Convert : k == ToolsKey ? WheelTarget.Tools : k == ActionsKey ? WheelTarget.Actions : null;

    private bool Bound(WheelKey k) => ConvertKey == k || ToolsKey == k || ActionsKey == k;

    private WheelKey? Gesture(ModifierKey key, bool shift, bool ctrl, bool alt, long now)
    {
        if (key == ModifierKey.Shift && ctrl || key == ModifierKey.Ctrl && shift) { Reset(); return WheelKey.ShiftCtrl; }
        if (key == ModifierKey.Shift && alt || key == ModifierKey.Alt && shift) { Reset(); return WheelKey.AltShift; }
        switch (key)
        {
            case ModifierKey.Shift:
                if (now - _lastShiftTap <= DoubleTapMs) { Reset(); return WheelKey.ShiftDouble; }
                _lastShiftTap = now;
                return WheelKey.Shift;
            case ModifierKey.Ctrl: return WheelKey.Ctrl;
            default: return WheelKey.Alt;
        }
    }

    /// <summary>Unbound gestures fall back to the plain key that was pressed (a double tap with no Tools binding is just Shift).</summary>
    private WheelTarget? Resolve(WheelKey g, ModifierKey pressed)
    {
        if (Bound(g)) return Target(g);
        var single = pressed switch { ModifierKey.Shift => WheelKey.Shift, ModifierKey.Ctrl => WheelKey.Ctrl, _ => WheelKey.Alt };
        return Target(single);
    }

    /// <summary>Human label used in Settings, the hub hint and the welcome screen.</summary>
    public static string Label(WheelKey k) => k switch
    {
        WheelKey.AltShift => "Alt+Shift",
        WheelKey.ShiftDouble => "Shift twice",
        WheelKey.ShiftCtrl => "Shift+Ctrl",
        _ => k.ToString(),
    };
}
