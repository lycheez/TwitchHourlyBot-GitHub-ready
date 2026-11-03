# Twitch Hourly Bot

A Windows desktop Twitch automation app built with C# and WPF on .NET 8.

## Features

- Twitch OAuth sign-in with session validation, token refresh, and disconnect controls.
- Monitor multiple Twitch channels and send a configured chat message after each channel has been live for a chosen number of hours.
- EventSub chat monitoring with per-channel keyword rules and automatic timeout durations.
- Per-channel wellness messages for hydration, eye breaks, stretching, relaxation, and encouragement, with category cooldowns.
- Dashboard, channel controls, settings, and an activity log.

## Requirements

- Windows
- .NET 8 SDK with the Windows Desktop workload, or Visual Studio 2022 with the .NET desktop development workload
- A Twitch application configured with the redirect URI `http://localhost:37821/callback/`

The app requests these Twitch scopes: `user:read:chat`, `user:write:chat`, and `moderator:manage:banned_users`. The bot account must be a moderator in channels where it performs keyword timeouts.

## Run

Open `TwitchHourlyBot.sln` in Visual Studio and build/run, or use `run.bat` from this folder after installing the .NET 8 SDK.

On first launch, enter your Twitch Client ID and Client Secret in the app and complete Twitch authorization. Credentials and OAuth session data are protected with Windows DPAPI for the current Windows user. Do not commit credentials, tokens, or local app data to this repository.

To create a Windows x64 self-contained release, use `publish-win-x64.bat`. The generated `publish/` folder is ignored by Git.

## Version

The included `VERSION.txt` identifies this source snapshot as `1.2.0`.
