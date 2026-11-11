# The protocol

Desk Protocol v0 is how an app tells Desk its name and title, sends notifications and opens other apps, and how
Desk tells the app about focus and quitting. The .NET client is
[`MiniTui.Desk.Protocol`](xref:MiniTui.Desk.Protocol.DeskClient). This page is for writing one in another language.

## Finding Desk

Desk sets two variables in each app's environment:

| Variable | Holds |
|---|---|
| `MINITUI_DESK_SOCKET` | The path of a Unix domain socket |
| `MINITUI_DESK_APP_ID` | This app's id in Desk, such as `notes-1` |

If either is missing, or the socket can't be reached, the app isn't in Desk, and should behave exactly as it does
outside it. Programs started from an app inherit them, so a client started from a shell in Desk connects as that
shell. An id can have several connections, and the newest one's name and title are shown until it closes.

## Lines

The socket carries newline-delimited JSON in UTF-8: one object per line, each with a `type`. A line longer than 64 KiB
closes the connection. Unknown types and unknown fields are ignored, and a client should ignore them too.

The app speaks first, with `hello`, and Desk ignores anything before it. Desk answers with `welcome`, which agrees the
version for the whole connection: the lower of the two.

```json
{"type":"hello","appId":"notes-1","name":"Notes","version":"0.1.0","protocol":0}
{"type":"welcome","protocol":0,"theme":"dark"}
{"type":"title","text":"groceries.md"}
{"type":"notify","id":"sync","title":"Synced","body":"12 notes","urgency":"low"}
```

## From the app

| Type | Fields | Means |
|---|---|---|
| `hello` | `appId`, `name`, `version`, `protocol` | The first line. `appId` is `MINITUI_DESK_APP_ID`'s, and `protocol` the highest version the app speaks. An id that isn't running closes the connection |
| `title` | `text` | A short status after the name, in the status line and the list of running apps. Control characters are removed |
| `notify` | `id`, `title`, `body`, `urgency`, `actions` | A notification. `urgency` is `low`, `normal` or `high`, and `actions` an optional list of `{"id", "label"}`. An `id` the app has sent before replaces that one |
| `launch` | `appId`, `args` | Opens a new instance of the app with this id in the launcher, with `args` after its own. Only from the app on the screen; otherwise, or for an unknown id, it's ignored |
| `quitDeclined` | | After a `quitRequest`, the user chose to stay |
| `bye` | | The app is closing the connection on purpose |

## From Desk

| Type | Fields | Means |
|---|---|---|
| `welcome` | `protocol`, `theme` | The version to use, and `light` or `dark` for the terminal's background |
| `focus` | `focused` | The app came to the screen or left it. Sent when the connection arrives and on every change |
| `notificationAction` | `id`, `action` | The user chose the action with this `id` from the notification with this `id` |
| `quitRequest` | | Please quit. The app exits, or asks the user and sends `quitDeclined` if they stay. Desk can always end it, if the user asks again |

## A client

A minimal client in Python: connect, say hello, set a title, and quit when asked.

```python
import json, os, socket

path, app_id = os.environ.get("MINITUI_DESK_SOCKET"), os.environ.get("MINITUI_DESK_APP_ID")
desk = None
if path and app_id:
    try:
        desk = socket.socket(socket.AF_UNIX)
        desk.connect(path)
    except OSError:
        desk = None  # not in Desk after all: carry on without it

def send(**message):
    if desk:
        desk.sendall((json.dumps(message) + "\n").encode())

send(type="hello", appId=app_id, name="Weather", version="0.1.0", protocol=0)
send(type="title", text="Oslo, 12°")

for line in (desk.makefile(encoding="utf-8") if desk else []):
    message = json.loads(line)
    if message.get("type") == "quitRequest":
        send(type="bye")
        break
```

A real one reads on a thread of its own, so the app isn't held up, and never lets a broken connection stop the app:
if the socket fails, the client goes quiet and the app carries on. The .NET client also drops messages rather than
block when Desk isn't reading.

## Not in v0

There's no clipboard, no drawing and no messages between apps. Each app draws in its own terminal, which handles
the clipboard as any terminal does, through OSC 52.
