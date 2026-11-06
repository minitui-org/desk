using MiniTui.Vt;

namespace MiniTui.Desk;

/// <summary>A running app, or one that failed and is keeping its last screen.</summary>
internal sealed class HostedApp(AppEntry entry)
{
    private volatile bool _wantsAttention;

    public AppEntry Entry { get; } = entry;
    public VtTerminal Terminal { get; set; } = null!; // set as it starts
    public string Name => Entry.DisplayName;

    /// <summary>How it ended, when that was a failure and its screen is staying.</summary>
    public PtyExit? Failure { get; set; }

    /// <summary>A bell has been reported and the user hasn't been back since, so more bells add nothing.</summary>
    public bool WantsAttention { get => _wantsAttention; set => _wantsAttention = value; }

    public string? Title
    {
        get
        {
            lock (Terminal.Lock) return Terminal.Screen.Title;
        }
    }

    /// <summary>Exit 0, or Ctrl+C, whether the program handled it (130) or died of it.</summary>
    public static bool EndedCleanly(PtyExit exit) => exit.Code is 0 or 130 || exit.Signal == 2;
}
