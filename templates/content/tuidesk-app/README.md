# MyApp

A [MiniTui](https://minitui.org) app that joins [MiniTui Desk](https://minitui.org/desk): it shows its name and title in
Desk's status line and sends Desk its notifications, and runs the same anywhere else.

```bash
dotnet run --project MyApp     # on its own
dotnet test                    # the tests, with ComponentHarness
```

## In Desk

Install it as a tool, then add it to Desk's launcher. Desk shows it from its next start.

The binary is built with Native AOT, which needs a C toolchain: `sudo apt install clang zlib1g-dev` on Debian or
Ubuntu, `sudo dnf install clang zlib-devel` on Fedora. The
[prerequisites](https://aka.ms/nativeaot-prerequisites) list the others.

```bash
dotnet pack MyApp -o nupkg                    # the top-level package
dotnet pack MyApp -r linux-x64 -o nupkg       # the Native AOT binary for this platform
dotnet tool install -g MyApp --add-source nupkg --version 0.1.0
tuidesk add myapp                             # reads the manifest in Program.cs
```

Run inside Desk without installing: `tuidesk add "$PWD/MyApp/bin/Debug/net10.0/myapp" --id my-app-id-dev --name "MyApp (dev)"`
after a `dotnet build`, which adds the build under its own id.

The package id is `MyApp`, in `MyApp/MyApp.csproj`. Before publishing it, make it one nobody else on nuget.org has;
`--version` above keeps a same-named package there from being picked instead of yours.

## What's where

- `MyApp/Program.cs`: `WithDesk`, with the manifest (`id`, `description`) that `tuidesk add` saves.
- `MyApp/MainScreen.cs`: the screen. The selection sets the title, and Enter sends a notification.
- `MyApp.Tests`: the screen in a `ComponentHarness`, with the title and notifications captured.

Docs: [minitui.org/desk](https://minitui.org/desk).
