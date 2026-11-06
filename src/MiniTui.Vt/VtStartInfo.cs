namespace MiniTui.Vt;

/// <summary>What to run in a terminal, and how.</summary>
public sealed record VtStartInfo(string FileName)
{
    /// <summary>The arguments, not including the program's name. By default, none.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>
    /// Changes to the inherited environment: a value sets the variable, and null removes it. <c>TERM</c> is
    /// <c>xterm-256color</c> and <c>COLORTERM</c> is <c>truecolor</c> unless set here.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>The working directory, or null to inherit it.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The width in cells. By default, 80.</summary>
    public int Width { get; init; } = 80;

    /// <summary>The height in cells. By default, 24.</summary>
    public int Height { get; init; } = 24;
}
