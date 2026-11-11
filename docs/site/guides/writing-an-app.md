# Writing an app

Make a [MiniTui](https://minitui.org) app that joins Desk: its title in the status line, notifications in Desk's
list, and other apps opened beside it. The same app runs anywhere else as an ordinary terminal app.

## From the template

```bash
dotnet new install MiniTui.Desk.Templates
dotnet new tuidesk-app -n Weather
cd Weather
dotnet run --project Weather
dotnet test
```

That makes:

```text
Weather/
  Weather.slnx
  README.md               how to run, test, install and add it
  Weather/
    Weather.csproj        a .NET tool, weather, with Native AOT
    Program.cs            App.Create().WithDesk(...)
    MainScreen.cs         a list and a notification
  Weather.Tests/
    MainScreenTests.cs    the screen in a ComponentHarness
```

The screen's selection is the app's title in Desk, and `Enter` sends a notification.

The command is the name in lowercase, `weather`, and so is the [manifest](manifest.md)'s id. `--id` sets another.

## Into Desk

Install it as a tool, and add it to the launcher:

```bash
dotnet pack Weather -o nupkg                    # the top-level package
dotnet pack Weather -r linux-x64 -o nupkg       # the binary for this platform
dotnet tool install -g Weather --add-source nupkg --version 0.1.0
tuidesk add weather
```

`tuidesk add` asks the app for its manifest, so it says `Added weather: Weather`, and Desk lists it from its next
start. While working on it, a build can be added under an id of its own, so it doesn't replace the installed one:

```bash
dotnet build
tuidesk add "$PWD/Weather/bin/Debug/net10.0/weather" --id weather-dev --name "Weather (dev)"
```

## WithDesk

One call joins Desk, from the [`MiniTui.Desk.Protocol`](https://www.nuget.org/packages/MiniTui.Desk.Protocol)
package:

```csharp
using MiniTui;
using MiniTui.Desk.Protocol;

var app = App.Create()
    .WithDesk("Weather", out var desk, manifest: new DeskManifest { Id = "weather", Description = "The forecast" });

app.Screens.Push(new MainScreen(desk));
app.Run();
```

[`WithDesk`](xref:MiniTui.Desk.Protocol.DeskAppExtensions.WithDesk*) connects when the app runs in Desk, and does
nothing otherwise. Its [`DeskClient`](xref:MiniTui.Desk.Protocol.DeskClient) is how the app talks to Desk. Every member
is safe to call outside Desk, where it does nothing, so the app needs no checks except where it wants to behave
differently. [`IsActive`](xref:MiniTui.Desk.Protocol.DeskClient.IsActive) says whether it's connected.

Events from Desk arrive on the app's thread, as if from a key press, so handlers can change the screen. Handlers added
before `app.Run()` hear everything.

## The title

```csharp
desk.SetTitle("Oslo, 12°");
```

The title follows the app's name in the status line and the list of running apps: the open file, the selection, or
whatever says what the app is on. Keep it short, as the status line is shared.

## Notifications

```csharp
desk.Notify(new DeskNotification("rain", "Weather", "Rain from 3 pm") { Urgency = DeskUrgency.High });
```

Desk counts each, and lists them on `Ctrl+N`. With Do Not Disturb off, `Normal` and `High` ones also appear for a
moment. The urgency is a request: the user's Do Not Disturb wins. A notification with the id of an earlier one from
the same app replaces it, so an app can keep one entry up to date rather than adding more.

Actions put choices on a notification, and the one chosen comes back to the app:

```csharp
desk.Notify(new DeskNotification("rain", "Weather", "Rain from 3 pm")
{
    Actions = [new DeskAction("umbrella", "Remind me at 2:30")],
});
desk.NotificationAction += (id, action) =>
{
    if (id == "rain" && action == "umbrella") RemindAt(new TimeOnly(14, 30));
};
```

Outside Desk there's nowhere for a notification to go. An app that wants one anyway can show a toast when the client
isn't active, as the template does:

```csharp
if (desk.IsActive) desk.Notify(notification);
else app.Toasts.Push(notification.Body);
```

## Focus

```csharp
desk.FocusChanged += focused =>
{
    if (focused) Reload();
    else Save();
};
```

Desk says when the app leaves the screen and comes back. Notes saves on the way out and reloads what changed on the
way back; an app that polls can pause. [`IsFocused`](xref:MiniTui.Desk.Protocol.DeskClient.IsFocused) is true outside
Desk.

## Quitting

When the user closes the app or quits Desk, Desk asks it to quit. With no handler, `WithDesk` calls `app.Quit()`. An
app with unsaved work can handle it and stay:

```csharp
desk.QuitRequested += () =>
{
    if (!TrySave()) desk.DeclineQuit();
};
```

[`DeclineQuit`](xref:MiniTui.Desk.Protocol.DeskClient.DeclineQuit) cancels the close, or Desk's quit, and the app
should then say why it stayed. Desk never kills an app that's asked without the user pressing the key again, so a slow
answer can't lose work.

## Opening another app

```csharp
desk.Launch("notes", "--append", "Weather", "Rain from 3 pm");
```

[`Launch`](xref:MiniTui.Desk.Protocol.DeskClient.Launch*) opens a new instance of the app with that id in the
launcher, with these arguments after its own, and brings it to the front. With two panes showing, it opens in the
other pane. It's honored only from the app on the screen, so an app in the background can't take it, and an id the
launcher doesn't have is ignored.

Reader sends a passage to Notes this way. Outside Desk, it runs `tuinotes` in its own terminal instead, until Notes
quits.

## The theme

[`Theme`](xref:MiniTui.Desk.Protocol.DeskClient.Theme) is `"light"` or `"dark"`: the terminal's background, as Desk
found it. `WithDesk` waits up to half a second for it, so it's normally known when the first screen draws. It's null
outside Desk, where MiniTui's own detection is the one to use.

## Testing

The screen gets the client's members as delegates rather than the client, so tests can capture them:

```csharp
var titles = new List<string>();
var notifications = new List<DeskNotification>();
using var harness = new ComponentHarness(new MainScreen(titles.Add, notifications.Add), width: 40, height: 10);

harness.Press("down");
harness.Press("enter");
Assert.Equal("You picked Bread", Assert.Single(notifications).Body);
```

MiniTui's [testing guide](https://minitui.org/guides/testing.html) covers `ComponentHarness`.

## Without WithDesk

[`DeskClient.Connect`](xref:MiniTui.Desk.Protocol.DeskClient.Connect*) connects without an app, for a program that
isn't built on MiniTui's `App`. Its events then arrive on a background thread, one at a time, and the client should be
disposed when the program ends. It doesn't write the manifest: that's `WithDesk`'s, or the program's own, as
[the manifest](manifest.md) says.
