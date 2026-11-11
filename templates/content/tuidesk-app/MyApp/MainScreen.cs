using MiniTui;
using MiniTui.Desk.Protocol;

namespace MyApp;

/// <summary>
/// A list, with the selection as the title in Desk's status line and Enter sending a notification: each part of Desk
/// once. Replace it with the app.
/// </summary>
internal sealed class MainScreen : Screen
{
    private readonly Action<string> _setTitle;
    private readonly Action<DeskNotification>? _notify;
    private readonly ItemList _list = new();
    private int _sent;

    public MainScreen(Action<string>? setTitle = null, Action<DeskNotification>? notify = null)
    {
        _setTitle = setTitle ?? (_ => { });
        _notify = notify;
        AddChild(_list);
        Focus.Add(_list);
        _list.State.SetRows(new[] { "Apples", "Bread", "Coffee", "Dates", "Eggs" }.Select(item => new FilterRow(item, Key: item)));
        _list.State.SelectionChanged += (_, _) => ShowSelected();

        Keys.Add("enter", "Notify", Send)
            .Add("ctrl+q", "Quit", () => App.Quit());
    }

    public override string Title => "MyApp";

    internal string? Selected => _list.State.Selected is { } row ? _list.State.Rows[row].Label : null;

    protected override void OnMount() => ShowSelected();

    public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Render(_list, buf, area);
    }

    // The title shows after the app's name in Desk's status line
    private void ShowSelected() => _setTitle(Selected ?? "");

    private void Send()
    {
        if (Selected is not { } item) return;
        var notification = new DeskNotification($"picked-{++_sent}", "MyApp", $"You picked {item}");
        if (_notify is { } notify) notify(notification);
        else App.Toasts.Push(notification.Body);
    }

    /// <summary>The list, filtered by typing.</summary>
    private sealed class ItemList : Component
    {
        public FilterListState State { get; } = new();

        public override bool CapturesText => true;

        public override bool HandleKey(ConsoleKeyInfo key) => State.HandleKey(key);

        public override bool HandlePaste(string text) => State.HandlePaste(text);

        public override bool HandleMouse(MouseEvent mouse) => State.HandleMouse(mouse);

        public override void Render(CellBuffer buf, Rect area, RenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            Widget.FilterList(State).Placeholder("Type to filter").Block(Widget.Block().Title(" MyApp ")).Focused(ctx.Focused).Draw(buf, area);
        }
    }
}
