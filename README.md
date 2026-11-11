# Twitch Hourly Bot

[![CI](https://github.com/lycheez/TwitchHourlyBot-GitHub-ready/actions/workflows/ci.yml/badge.svg)](https://github.com/lycheez/TwitchHourlyBot-GitHub-ready/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A Windows desktop Twitch automation app built with C# and WPF on .NET 8.

## Features

- Twitch OAuth sign-in with session validation, token refresh, and disconnect controls.
- Monitor multiple Twitch channels and send a configured chat message after each channel has been live for a chosen number of hours.
- EventSub chat monitoring with per-channel keyword rules and automatic timeout durations. There are no built-in keywords: add your own per channel.
- Per-channel wellness messages for hydration, eye breaks, stretching, relaxation, and encouragement, with category cooldowns.
- Dashboard, channel controls, settings, and an activity log.

## Requirements

- Windows
- .NET 8 SDK with the Windows Desktop workload, or Visual Studio 2022 with the .NET desktop development workload
- A Twitch application configured with the redirect URI `http://localhost:37821/callback/`

The app requests these Twitch scopes: `user:read:chat`, `user:write:chat`, and `moderator:manage:banned_users`. The bot account must be a moderator in channels where it performs keyword timeouts.

## Set up a Twitch application

1. Register an application in the [Twitch Developer Console](https://dev.twitch.tv/console).
2. Add `http://localhost:37821/callback/` as an OAuth Redirect URL (the trailing slash matters).
3. Copy the application's Client ID and generate a Client Secret.
4. Enter both in the app on first launch and complete the Twitch authorization in your browser.

## Run

Run the tests with `dotnet test TwitchHourlyBot.sln`.

Open `TwitchHourlyBot.sln` in Visual Studio and build/run, or use `run.bat` from this folder after installing the .NET 8 SDK.

To create a Windows x64 self-contained release, use `publish-win-x64.bat`. The generated `publish/` folder is ignored by Git.

## Try the web preview (Windows)

Double-click `start-web.bat` (needs the .NET 8 SDK and Node.js LTS). It starts the API on port 5000 and the dashboard on port 4200 and opens http://localhost:4200. In VS Code you can instead run the task **Start web app (API + dashboard)** (Terminal > Run Task).

### Using the bot from the web dashboard

The API can run the bot itself (hourly "live for N hours" messages, wellness events, keyword timeouts), so the dashboard can do what the desktop app does:

1. In your Twitch developer application, add the OAuth redirect URL shown in the dashboard (for local development with `start-web.bat`: `http://localhost:4200/api/auth/callback`).
2. Enter the Client ID and Client Secret in the dashboard and save. The secret is encrypted on the server and is never sent back to the browser.
3. Click **Log in with Twitch**, then **Start bot**.

Status: early preview. It has automated tests against a fake Twitch but has not been run against real Twitch by the author yet, the API has no login of its own (use it on your own machine only), and event messages use the built-in defaults.

## Project layout

- `src/TwitchHourlyBot.Core` - UI-independent logic (net8.0): auth, Twitch client, keyword service, event engine, models.
- `src/TwitchHourlyBot` - the WPF desktop app.
- `src/TwitchHourlyBot.Api` - ASP.NET Core Web API (SQLite) for channels and keyword rules; Swagger at `/swagger`.
- `web` - Angular dashboard for that API (see `web/README.md`).
- `tests/TwitchHourlyBot.Tests` - xUnit tests for the core library.

## How it works

| Component | Responsibility |
| --- | --- |
| `TwitchAuthService` | OAuth authorization-code flow using a local `HttpListener` callback, `state` check, token validation, refresh (handles rotated refresh tokens) and revoke. |
| `TwitchClient` | Thin wrapper over the Twitch Helix API (users, streams, chat messages, timeouts). Retries a request once after re-authenticating when Twitch answers `401`. |
| `KeywordTimeoutService` | Connects to Twitch EventSub over WebSocket, subscribes to `channel.chat.message` for each enabled channel and times out chatters whose message contains an enabled keyword. Broadcasters, moderators and the bot itself are skipped; timeouts are clamped to 1-600 seconds and de-duplicated per user and keyword. Reconnects with exponential backoff (3 s up to 30 s). |
| `EventEngine` | Picks a wellness message for a channel, respecting per-category cooldowns and avoiding an immediate repeat of the last message in a category. |
| `SecureStorage` | Windows DPAPI wrapper (P/Invoke) used to protect the client secret and the saved OAuth session. |
| Stores | Settings, stream state and cooldown state are JSON files under `%LocalAppData%\TwitchHourlyBot`. |

## Security notes

- Credentials and OAuth session data are protected with Windows DPAPI for the current Windows user. Do not commit credentials, tokens, or local app data to this repository.
- The OAuth `state` parameter is generated per login and verified on the callback.
- `.gitignore` excludes build output, `appsettings*.json` and `.env*` files.

## Known limitations

- The desktop app is Windows only (WPF and DPAPI). The web API and dashboard are an early preview: no authentication and no Twitch connection yet, so run them locally only.
- Test coverage is partial: unit tests cover keyword matching, event selection/cooldowns and the Twitch client's 401 retry. OAuth, EventSub and the UI are not covered yet.
- Much of the application logic lives in `MainWindow.xaml.cs` and in static stores, which makes it hard to unit test. Separating these is planned.
- User-facing strings are currently Thai only.

## Version

The included `VERSION.txt` identifies this source snapshot as `1.3.0`.

## License

[MIT](LICENSE)
