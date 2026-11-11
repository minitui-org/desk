# MiniTui.Desk.Protocol

Makes a [MiniTui](https://minitui.org) app a native app of [MiniTui Desk](https://www.nuget.org/packages/MiniTui.Desk), with one call:

```csharp
var app = App.Create().WithDesk("Weather", out var desk);
desk.SetTitle("Oslo, 4°");
desk.Notify(new DeskNotification("rain", "Weather", "Rain in an hour"));
```

Inside Desk, the app's name and title show in the status line, its notifications go to Desk's queue, it can open other apps, and it hears when it gains or loses the focus and when Desk asks it to quit. Outside Desk every member does nothing, so the app behaves the same with no checks. Trim- and AOT-safe, and depends on MiniTui only.

Docs: [minitui.org/desk](https://minitui.org/desk). The protocol: [spec §8](https://github.com/minitui-org/desk/blob/main/docs/SPEC.md#8-desk-protocol-v0).
