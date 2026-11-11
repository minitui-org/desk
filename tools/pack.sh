#!/usr/bin/env bash
# Packs Desk's packages (spec §16.3) into a folder, for CI and publish.yml.
#
#   tools/pack.sh <folder> any [msbuild args...]     the libraries, and each tool's top-level package
#   tools/pack.sh <folder> <rid> [msbuild args...]   each tool's Native AOT package for that platform
#
# A tool's AOT package has to be built on its own platform, so linux-arm64 is packed on an arm64 machine.
set -euo pipefail

out=$1
what=$2
shift 2
cd "$(dirname "$0")/.."

libraries=(src/MiniTui.Desk.Protocol src/MiniTui.Vt)
tools=(src/MiniTui.Desk apps/Notes apps/Tasks apps/Reader)

if [[ $what == any ]]; then
  for project in "${libraries[@]}" "${tools[@]}"; do
    dotnet pack "$project" -c Release -o "$out" "$@"
  done
else
  for project in "${tools[@]}"; do
    dotnet pack "$project" -c Release -r "$what" -o "$out" "$@"
  done
fi
