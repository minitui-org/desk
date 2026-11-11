---
_layout: landing
title: A desktop in the terminal
---

<div class="mt-hero">
  <h1>MiniTui Desk</h1>
  <p class="mt-tagline">A distraction-free desktop in the terminal, for Linux.</p>
  <p class="mt-lede">Run your shell, your editor and other terminal programs in Desk, one or two at a time, with one
  status line and one key of its own. Apps built with <a href="https://minitui.org">MiniTui</a> join it with one call,
  to show a title, send notifications and open each other. It installs as a .NET tool, on x64 or arm64.</p>
  <div class="mt-install"><code>dotnet tool install -g MiniTui.Desk</code></div>
  <div class="mt-actions">
    <a class="btn btn-primary btn-lg" href="guides/using-desk.md">Use Desk</a>
    <a class="btn btn-outline-secondary btn-lg" href="guides/writing-an-app.md">Write an app</a>
    <a class="btn btn-outline-secondary btn-lg" href="https://github.com/minitui-org/desk">GitHub</a>
  </div>
</div>

<div class="mt-features">
  <div class="mt-feature">
    <h3>Any terminal program</h3>
    <p>Each app runs in a terminal of its own, so a shell, Neovim, htop or k9s works as it does anywhere. A launcher
    starts them, and two can share the screen. Notes, Tasks and Reader are the first apps made for it.</p>
    <a href="guides/using-desk.md">Using Desk</a>
  </div>
  <div class="mt-feature">
    <h3>Apps that know they're in Desk</h3>
    <p><code>dotnet new tuidesk-app</code> makes a MiniTui app that puts its title in the status line, sends
    notifications and opens other apps, with tests, packed as a .NET tool. Outside Desk it runs as before.</p>
    <a href="guides/writing-an-app.md">Writing an app</a>
  </div>
  <div class="mt-feature">
    <h3>An open protocol</h3>
    <p>Newline-delimited JSON over a Unix socket, and a manifest any program can print. A client in any language can
    do what the .NET one does.</p>
    <a href="guides/protocol.md">The protocol</a>
  </div>
</div>
