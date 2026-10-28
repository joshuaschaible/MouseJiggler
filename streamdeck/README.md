# Mouse Jiggler for Stream Deck

The **Toggle Mouse Jiggler** action controls the app directly and displays its real state. It uses the same power symbol and violet/cyan accents as the app.

| Button | Meaning |
|---|---|
| **ACTIVE** with a violet/cyan ring | Jiggling is on. Press to stop. |
| **IDLE** with a muted ring | Jiggling is off. Press to start. |
| **OFFLINE** with a muted icon | The app is closed or unavailable. Press to open it in the tray and start jiggling. |

Changing the app through its window, tray menu, or keyboard shortcut updates the Stream Deck button within about half a second. Pressing the key never moves or focuses the app window. A closed app starts with `--background`, which opens hidden and idle, before one toggle turns it on.

## Install

1. Install the companion Mouse Jiggler app from this repository.
2. Open the packaged `com.badbadrobot.mousejiggler.streamDeckPlugin` file to install the plugin.
3. Drag **Mouse Jiggler → Toggle Mouse Jiggler** onto a free Stream Deck key.
4. Leave **App location** blank if the app is already running. If the app is in a different folder and has not been run yet, enter its full `MouseJiggler.exe` path in the action settings.

The plugin requires Windows 10 or later and Stream Deck 6.9 or later. It uses Stream Deck's bundled Node20 runtime and includes its JavaScript dependency; users do not need Node.js or npm installed separately.

## Build and check

From the project directory, run:

```powershell
.\streamdeck\Test-Plugin.ps1
.\streamdeck\Build-Plugin.ps1
```

Tests use private test pipes, an isolated local WebSocket peer, and mocked app state. They do not control the real Mouse Jiggler app or modify production preferences. The tests cover live status synchronization, multiple keys, single-press behavior, offline launch, lost toggle responses, malformed responses, and request timeouts. Local WebSocket access must be permitted for the protocol test.

The build is copied to `artifacts/streamdeck/com.badbadrobot.mousejiggler.sdPlugin`.

Package it with the [official Stream Deck CLI](https://docs.elgato.com/streamdeck/cli/intro/):

```powershell
.\streamdeck\Pack-Plugin.ps1
```

If the CLI is installed locally rather than on your command path, pass `-StreamDeckCli` with its `bin/streamdeck.mjs` path. The build and package outputs stay under the ignored `artifacts/` directory.

## Local connection

The companion app publishes `%LOCALAPPDATA%\MouseJiggler\streamdeck.json` and serves a named pipe restricted to the current Windows user/session. The plugin reads the descriptor, connects locally, and sends one newline-terminated `status` or `toggle` command. Every displayed state comes from a successful app reply. There is no HTTP listener or external service.

Requests are serialized and bounded. A toggle whose reply is lost is never retried, because it may have succeeded. The plugin reads status afterward to recover the actual result. Errors display **OFFLINE** and an alert until a valid response is available; it never assumes the app switched on.

The runtime uses Elgato's documented [WebSocket registration/events](https://docs.elgato.com/streamdeck/sdk/references/websocket/plugin/) and [property inspector interface](https://docs.elgato.com/streamdeck/sdk/references/websocket/ui/). `DisableAutomaticStates` ensures that the app, rather than an independent key counter, drives the displayed on/off state.

The pinned MIT-licensed `ws` dependency and integrity details are included in `THIRD-PARTY-NOTICES.txt` and `vendor/ws/LICENSE`.
