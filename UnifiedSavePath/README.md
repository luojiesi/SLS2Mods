# UnifiedSavePath on macOS (Apple Silicon)

This version targets the Slay the Spire 2 v0.108 mod loader. It is a code-only
mod: the release contains `UnifiedSavePath.dll` and `UnifiedSavePath.json`, and
does not contain a `.pck` file.

## Build and package

Install the .NET 9 SDK, then run:

```sh
brew install dotnet@9
./UnifiedSavePath/build-macos.sh \
  "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
```

The script automatically detects Homebrew's keg-only `dotnet@9`; no PATH or
`DOTNET_ROOT` changes are required.

The zip package is written to `dist/`. You may instead set `STS2_GAME_DIR` and
run the script without an argument. The project also accepts
`-p:STS2GameDataDir=/absolute/path/to/data_sts2_macos_arm64` for non-standard
layouts.

## Install or upgrade

Quit the game first. The macOS mods directory is:

```text
<game>/SlayTheSpire2.app/Contents/MacOS/mods/
```

Back up and remove every older copy of UnifiedSavePath, including its `.dll`,
`.json`, and `.pck`. Do not leave the old PCK behind. Extract the release so the
result is one directory containing only:

```text
mods/UnifiedSavePath/UnifiedSavePath.dll
mods/UnifiedSavePath/UnifiedSavePath.json
```

If the game does not start, remove the new `UnifiedSavePath` directory and
restore the backup. The current log is located at:

```text
~/Library/Application Support/SlayTheSpire2/logs/godot.log
```

Successful startup should log discovery of `UnifiedSavePath.json`, loading of
`UnifiedSavePath.dll`, and completion of `UnifiedSavePathMod.Initialize`, with
no Harmony or `AmbiguousMatchException` error. Save log entries should use
`steam/<steam-id>/profile1/...`, without a `modded` path component.

Although macOS `file` may describe a managed DLL as a PE/Windows assembly, this
DLL contains platform-independent .NET IL and is loaded by the game's .NET
runtime.
