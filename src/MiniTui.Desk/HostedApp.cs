using MiniTui.Vt;

namespace MiniTui.Desk;

/// <summary>A running app, or one that failed and is keeping its last screen.</summary>
internal sealed class HostedApp(AppEntry entry, string id)
{
    private volatile bool _wantsAttention;

    public AppEntry Entry { get; } = entry;

    /// <summary>For the protocol: the entry's id and an instance number, <c>notes-1</c>. A restart keeps it.</summary>
    public string Id { get; } = id;

    public VtTerminal Terminal { get; set; } = null!; // set as it starts

    /// <summary>Open connections that have said hello, oldest first (spec §8.1). UI thread only.</summary>
    public List<DeskConnection> Connections { get; } = [];

    /// <summary>The connection that speaks for it: the most recent hello.</summary>
    public DeskConnection? Native => Connections.Count > 0 ? Connections[^1] : null;

    public string Name => Native is { Name.Length: > 0 } native ? native.Name : Entry.DisplayName;

    /// <summary>How it ended, when that was a failure and its screen is staying.</summary>
    public PtyExit? Failure { get; set; }

    /// <summary>A bell has been reported and the user hasn't been back since, so more bells add nothing.</summary>
    public bool WantsAttention { get => _wantsAttention; set => _wantsAttention = value; }

    /// <summary>Ctrl+X has asked it to quit, so pressing it again forces it.</summary>
    public bool QuitAsked { get; set; }

    /// <summary>The protocol's title, while it has one, otherwise the terminal's (OSC 0/2).</summary>
    public string? Title
    {
        get
        {
            if (Native?.Title is { } title) return title;
            lock (Terminal.Lock) return Terminal.Screen.Title;
        }
    }

    /// <summary>Exit 0, or Ctrl+C, whether the program handled it (130) or died of it.</summary>
    public static bool EndedCleanly(PtyExit exit) => exit.Code is 0 or 130 || exit.Signal == 2;
}
