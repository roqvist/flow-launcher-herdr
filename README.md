# Flow Launcher plugin: Herdr

A Flow Launcher plugin that lists running [Herdr](https://herdr.dev) agents
and panes across all your named sessions, and lets you send them
prompts/commands directly from Flow Launcher.

## Usage

- Action keyword: `he`
- Open the plugin settings (Flow Settings → Plugins → Herdr) if `herdr` is
  not on PATH, and set the full path to the binary.
- `he` (empty or with filter text) lists agents and panes from all running
  sessions, one row per agent/pane, labeled with session and title.
- Pressing Enter on a row fills the search box with
  `he a:<session>/<name-or-id> ` (agent) or `he p:<session>/<pane-id> `
  (pane). Type the prompt/command and press Enter again to send it.
- Right-click/context menu: focus the agent/pane in Herdr, copy its
  name/ID, or (for panes) **Rename pane**.

## Build

```
dotnet publish src\Flow.Launcher.Plugin.Herdr -c Release -r win-x64 --no-self-contained

powershell -NoProfile -Command ^
  "Compress-Archive -Path 'src\Flow.Launcher.Plugin.Herdr\bin\Release\win-x64\publish\*' -DestinationPath 'dist\Flow.Launcher.Plugin.Herdr.zip' -Force"
```
