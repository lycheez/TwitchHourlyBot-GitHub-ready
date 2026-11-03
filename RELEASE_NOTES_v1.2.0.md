# Twitch Hourly Bot v1.2.0

## Keyword Timeout Control Center

- Added a dedicated **Keyword Timeout** tab.
- Keyword rules are stored per channel.
- Each channel can enable/disable Keyword Protection independently.
- Add, remove, enable/disable keywords from the UI.
- Configure timeout duration per keyword (30/60/120/300 seconds in the UI; service clamps to 1–600 seconds).
- Existing v1.1.0 behavior is migrated: channels without keyword rules receive `เนเน่` with 60 seconds enabled.
- Matching remains **Contains** and case-insensitive.
- Broadcaster, moderators, and the bot account remain protected.
- Activity log records matched keyword and timeout duration.
- Existing OAuth, chat, scheduler, event engine, and dashboard flow retained.
