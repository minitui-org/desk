# Tasks

`tuitasks`: a to-do list with due dates, in a [todo.txt](https://github.com/todotxt/todo.txt) file, that reminds you
when something falls due. It runs on its own or inside `tuidesk`, where the status line shows what's overdue and
reminders are Desk notifications you can mark done or snooze. Spec: [`docs/SPEC.md`](../../docs/SPEC.md) §13.9.

```bash
dotnet run --project apps/Tasks
```

The list is `$XDG_DATA_HOME/tuitasks/todo.txt`, normally `~/.local/share/tuitasks/todo.txt`, one task per line, so
`todo.sh`, editor plugins and phone apps for todo.txt can use it too. Tasks reads `due:2026-11-20` and adds
`at:15:00` for a time on that day, which other tools ignore. Every change goes straight to the file and touches only
its own line. If a line changed elsewhere first, Tasks leaves it and shows the file as it is now.

```
(A) 2026-11-01 Call the dentist +health due:2026-11-09 at:15:00
2026-11-03 Buy milk due:2026-11-08
Read the k3s upgrade notes
x 2026-11-07 2026-11-01 Renew passport
```

The list shows tasks with due dates first, by when they're due, then the rest by priority, then the done ones. Each
row has its due time on the right: `Overdue · Nov 5` in red, `Today 15:00`, `Tomorrow`, `Fri`. Done tasks are muted,
with `Done · Nov 6` on the right.

| Key | Does |
|---|---|
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | Select a task |
| Typing | Filter. `Esc` clears the filter |
| Click, double-click | Select a task; edit it |
| `Ctrl+N` | New task, starting from the filter if you typed one |
| `Enter` | Edit the task's text, with its priority and tags |
| `Ctrl+D` | Done, or not done again |
| `Ctrl+T` | When it's due: `today`, `tomorrow`, `fri`, `+3d`, `+2w` or `2026-11-20`, then a time if you like, `15:00`. Empty clears it |
| `Delete` | Delete the selected task, after asking |
| `F1` | Show the keys |
| `Ctrl+Q` | Quit |

**Reminders:** a task is due at its time, or at 09:00 with only a date. While Tasks is running, a task falling due
notifies once, with **Done** and **Snooze 1 hour**; snoozing writes `snooze:` to the line. When it starts, anything
already overdue is one notification. Nothing reminds you while Tasks isn't running. Outside Desk reminders are toasts.

Tasks uses the mouse, so to select the terminal's own text, hold Shift while you drag.

To try it inside Desk, add it to `~/.config/tuidesk/apps.json`:

```json
[{ "id": "tasks", "name": "Tasks", "command": "/path/to/tuitasks" }]
```

## Gaps in MiniTui

What Tasks needed from MiniTui and didn't find (spec §13.1), each a MiniTui issue:

- **Filter list rows can't be styled** ([#88](https://github.com/minitui-org/minitui/issues/88)). Fixed in MiniTui
  2.4.0: done tasks are muted and an overdue time is red, where at first done tasks started with `✓`. An overdue
  one still says `Overdue` as well, so it doesn't rest on color alone.
