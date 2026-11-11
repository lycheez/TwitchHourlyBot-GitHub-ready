# Twitch Hourly Bot v1.3.0

## Changes

- **No default keyword.** New channels start with an empty keyword list; keyword rules are only the ones you add. Rules already saved on your machine are kept.
- Core logic (Twitch auth, Helix client, EventSub keyword service, event engine, models) moved into a reusable `TwitchHourlyBot.Core` library. The WPF app behaves as before.
- Added an xUnit test project covering keyword matching, event selection and cooldowns, and the Twitch client's 401 refresh-and-retry.
- Added GitHub Actions CI that builds and runs the tests on every push and pull request.
- Added MIT license and an expanded README.
