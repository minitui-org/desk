using System.Text;

namespace MiniTui.Vt;

/// <summary>What the parser found in the byte stream.</summary>
internal interface IVtParserHandler
{
    void Print(Rune rune);
    void Execute(byte control);
    void EscDispatch(byte intermediate, byte final);
    void CsiDispatch(VtParams parameters, byte marker, byte intermediate, byte final);
    void OscDispatch(ReadOnlySpan<byte> data, bool bel);
}

/// <summary>The parameters of a control sequence. Reused between sequences.</summary>
internal sealed class VtParams
{
    private const int Missing = -1;
    private const int MaxValue = 65535;
    public const int MaxCount = 32;

    private readonly int[] _values = new int[MaxCount];
    private uint _subMask;

    public int Count { get; private set; }

    /// <summary>The value at <paramref name="index"/>, or <paramref name="fallback"/> if it's absent or empty.</summary>
    public int Get(int index, int fallback = 0) =>
        index < Count && _values[index] != Missing ? _values[index] : fallback;

    /// <summary>The value at <paramref name="index"/>, treating absent, empty and 0 as 1, as counts do.</summary>
    public int Count1(int index)
    {
        var value = Get(index);
        return value == 0 ? 1 : value;
    }

    /// <summary>True if this value followed a colon, so it belongs to the value before it.</summary>
    public bool IsSub(int index) => index < Count && (_subMask & (1u << index)) != 0;

    public void Clear()
    {
        Count = 0;
        _subMask = 0;
    }

    public void Digit(int digit)
    {
        EnsureFirst();
        ref var value = ref _values[Count - 1];
        value = value == Missing ? digit : Math.Min(value * 10 + digit, MaxValue);
    }

    public void Separator(bool sub)
    {
        EnsureFirst();
        if (Count == MaxCount)
        {
            // Further values would overwrite the last one, so start ignoring them by keeping it full.
            _values[Count - 1] = MaxValue;
            return;
        }
        _values[Count] = Missing;
        if (sub) _subMask |= 1u << Count;
        Count++;
    }

    private void EnsureFirst()
    {
        if (Count != 0) return;
        _values[0] = Missing;
        Count = 1;
    }
}

/// <summary>
/// Turns a byte stream into printable characters and control functions. State survives between
/// calls to <see cref="Feed"/>, so a split sequence or UTF-8 character parses the same. Follows the
/// shape of Paul Williams' DEC parser; any byte is legal input.
/// </summary>
internal sealed class VtParser(IVtParserHandler handler)
{
    private const byte Bel = 0x07, Can = 0x18, Sub = 0x1A, Esc = 0x1B, Del = 0x7F;
    private const int MaxOsc = 1 << 20;

    private enum State { Ground, Escape, EscapeIntermediate, Csi, CsiIgnore, Osc, StringIgnore }

    private readonly VtParams _params = new();
    private readonly List<byte> _osc = [];
    private State _state;
    private byte _marker;
    private byte _intermediate;
    private bool _tooManyIntermediates;
    private bool _oscOverflow;

    // UTF-8 decoding, per Unicode table 3-7, so a bad sequence gives one U+FFFD per maximal subpart.
    private int _utf8Remaining;
    private int _utf8Value;
    private byte _utf8Low = 0x80;
    private byte _utf8High = 0xBF;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            switch (_state)
            {
                case State.Ground: Ground(b); break;
                case State.Escape: Escape(b); break;
                case State.EscapeIntermediate: EscapeIntermediate(b); break;
                case State.Csi: Csi(b); break;
                case State.CsiIgnore: CsiIgnore(b); break;
                case State.Osc: Osc(b); break;
                case State.StringIgnore: StringIgnore(b); break;
            }
        }
    }

    private void Ground(byte b)
    {
        if (_utf8Remaining != 0)
        {
            if (b >= _utf8Low && b <= _utf8High)
            {
                _utf8Value = (_utf8Value << 6) | (b & 0x3F);
                _utf8Low = 0x80;
                _utf8High = 0xBF;
                if (--_utf8Remaining == 0) handler.Print(new Rune(_utf8Value));
                return;
            }
            // The sequence ended early. This byte is looked at again on its own.
            _utf8Remaining = 0;
            _utf8Low = 0x80;
            _utf8High = 0xBF;
            handler.Print(Rune.ReplacementChar);
        }

        if (b is >= 0x20 and < Del) handler.Print(new Rune(b));
        else if (b < 0x20) Control(b);
        else if (b >= 0x80) Utf8Start(b);
    }

    private void Utf8Start(byte b)
    {
        switch (b)
        {
            case >= 0xC2 and <= 0xDF: Utf8Begin(1, b & 0x1F); break;
            case 0xE0: _utf8Low = 0xA0; Utf8Begin(2, 0); break;
            case 0xED: _utf8High = 0x9F; Utf8Begin(2, 0xD); break;
            case >= 0xE1 and <= 0xEF: Utf8Begin(2, b & 0x0F); break;
            case 0xF0: _utf8Low = 0x90; Utf8Begin(3, 0); break;
            case 0xF4: _utf8High = 0x8F; Utf8Begin(3, 4); break;
            case >= 0xF1 and <= 0xF3: Utf8Begin(3, b & 0x07); break;
            default: handler.Print(Rune.ReplacementChar); break;
        }
    }

    private void Utf8Begin(int remaining, int value)
    {
        _utf8Remaining = remaining;
        _utf8Value = value;
    }

    /// <summary>C0 controls act immediately, even inside a control sequence.</summary>
    private void Control(byte b)
    {
        switch (b)
        {
            case Esc:
                _state = State.Escape;
                _intermediate = 0;
                _tooManyIntermediates = false;
                break;
            case Can or Sub:
                _state = State.Ground;
                break;
            default:
                handler.Execute(b);
                break;
        }
    }

    private void Escape(byte b)
    {
        switch (b)
        {
            case < 0x20:
                Control(b);
                break;
            case < 0x30:
                _intermediate = b;
                _state = State.EscapeIntermediate;
                break;
            case (byte)'[':
                _params.Clear();
                _marker = 0;
                _intermediate = 0;
                _tooManyIntermediates = false;
                _state = State.Csi;
                break;
            case (byte)']':
                _osc.Clear();
                _oscOverflow = false;
                _state = State.Osc;
                break;
            case (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_':
                _state = State.StringIgnore;
                break;
            case < Del:
                _state = State.Ground;
                handler.EscDispatch(0, b);
                break;
        }
    }

    private void EscapeIntermediate(byte b)
    {
        switch (b)
        {
            case < 0x20:
                Control(b);
                break;
            case < 0x30:
                _tooManyIntermediates = true;
                break;
            case < Del:
                _state = State.Ground;
                if (!_tooManyIntermediates) handler.EscDispatch(_intermediate, b);
                break;
        }
    }

    private void Csi(byte b)
    {
        switch (b)
        {
            case < 0x20:
                Control(b);
                break;
            case < 0x30:
                if (_intermediate != 0) _state = State.CsiIgnore;
                _intermediate = b;
                break;
            case <= (byte)'9' when _intermediate == 0:
                _params.Digit(b - '0');
                break;
            case (byte)':' or (byte)';' when _intermediate == 0:
                _params.Separator(sub: b == ':');
                break;
            case >= 0x3C and <= 0x3F when _params.Count == 0 && _marker == 0 && _intermediate == 0:
                _marker = b;
                break;
            case < 0x40:
                _state = State.CsiIgnore;
                break;
            case < Del:
                _state = State.Ground;
                handler.CsiDispatch(_params, _marker, _intermediate, b);
                break;
        }
    }

    private void CsiIgnore(byte b)
    {
        if (b < 0x20) Control(b);
        else if (b is >= 0x40 and < Del) _state = State.Ground;
    }

    private void Osc(byte b)
    {
        switch (b)
        {
            case Bel:
                _state = State.Ground;
                DispatchOsc(bel: true);
                break;
            case Esc:
                // ESC \ ends it, and any other ESC sequence also ends it and then runs.
                DispatchOsc(bel: false);
                Control(b);
                break;
            case Can or Sub:
                _state = State.Ground;
                break;
            case < 0x20:
                break;
            default:
                if (_osc.Count < MaxOsc) _osc.Add(b);
                else _oscOverflow = true;
                break;
        }
    }

    private void DispatchOsc(bool bel)
    {
        if (!_oscOverflow) handler.OscDispatch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_osc), bel);
    }

    private void StringIgnore(byte b)
    {
        if (b is Esc or Can or Sub) Control(b);
    }
}
