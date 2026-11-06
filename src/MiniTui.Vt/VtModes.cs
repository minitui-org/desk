namespace MiniTui.Vt;

/// <summary>Which mouse events the child asked for.</summary>
public enum VtMouseMode
{
    /// <summary>No mouse reporting.</summary>
    None,
    /// <summary>Mode 9: presses only.</summary>
    X10,
    /// <summary>Mode 1000: presses and releases.</summary>
    Normal,
    /// <summary>Mode 1002: also motion while a button is held.</summary>
    ButtonEvent,
    /// <summary>Mode 1003: all motion.</summary>
    AnyEvent
}

/// <summary>How the child asked for mouse events to be encoded.</summary>
public enum VtMouseEncoding
{
    /// <summary>The original encoding, limited to column and row 223.</summary>
    Default,
    /// <summary>Mode 1005: coordinates as UTF-8.</summary>
    Utf8,
    /// <summary>Mode 1006: SGR, <c>CSI &lt; b;x;y M</c>.</summary>
    Sgr,
    /// <summary>Mode 1015: urxvt, <c>CSI b;x;y M</c>.</summary>
    Urxvt
}

/// <summary>The terminal modes the child has set, which decide how input is encoded for it.</summary>
/// <param name="AlternateScreen">The alternate screen is showing.</param>
/// <param name="ApplicationCursorKeys">Cursor keys send <c>SS3</c> forms (DECCKM).</param>
/// <param name="ApplicationKeypad">The keypad sends application forms (DECKPAM).</param>
/// <param name="BracketedPaste">Pastes are wrapped (mode 2004).</param>
/// <param name="Mouse">The mouse events the child asked for.</param>
/// <param name="MouseEncoding">How mouse events are encoded.</param>
/// <param name="KeyboardFlags">The kitty keyboard flags in effect.</param>
/// <param name="SynchronizedOutput">The child is partway through a frame (mode 2026).</param>
/// <param name="FocusReporting">The child wants to be told when it gains and loses focus (mode 1004).</param>
public readonly record struct VtModes(
    bool AlternateScreen,
    bool ApplicationCursorKeys,
    bool ApplicationKeypad,
    bool BracketedPaste,
    VtMouseMode Mouse,
    VtMouseEncoding MouseEncoding,
    int KeyboardFlags,
    bool SynchronizedOutput,
    bool FocusReporting);
