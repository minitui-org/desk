# MiniTui.Desk.Templates

`dotnet new tuidesk-app`: a [MiniTui](https://minitui.org) app that joins [MiniTui Desk](https://www.nuget.org/packages/MiniTui.Desk), with tests, packed as a .NET tool with a Native AOT binary per platform.

```bash
dotnet new install MiniTui.Desk.Templates
dotnet new tuidesk-app -n Weather          # --id sets the id Desk lists it by, weather by default
cd Weather && dotnet test
```

The app shows its name and title in Desk's status line and sends Desk its notifications, and runs the same anywhere else. Its README says how to install it and `tuidesk add` it.

Docs: [minitui.org/desk](https://minitui.org/desk).
