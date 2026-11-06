namespace MiniTui.Vt;

/// <summary>What a <see cref="VtScreen"/> needs from whatever is hosting it. Called during <see cref="VtScreen.Feed"/>.</summary>
public interface IVtHost
{
    /// <summary>Send an answer to one of the program's queries back to it. Must not wait for the program to read it.</summary>
    void Reply(ReadOnlySpan<byte> bytes);

    /// <summary>The program asked to put text on the clipboard (OSC 52).</summary>
    void SetClipboard(string text);

    /// <summary>The program rang the bell.</summary>
    void Bell();

    /// <summary>The program sent a notification (OSC 9 or OSC 777).</summary>
    /// <param name="title">The title, or null when there's none.</param>
    /// <param name="body">The text, with control characters removed.</param>
    void Notify(string? title, string body);
}
