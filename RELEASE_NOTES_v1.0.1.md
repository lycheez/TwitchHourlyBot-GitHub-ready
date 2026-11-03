# Twitch Hourly Bot v1.0.1

## Scope
UI/status fixes only. This release does not intentionally change OAuth, Chat, or Scheduler logic.

## Fixes
1. Channel live indicator
   - Added `ChannelConfig.IsLive` state.
   - The dot before the channel name is gray when offline/unknown/error and green when Twitch reports the channel as live.
   - Live state is updated in `CheckChannelAsync` only.

2. Event Settings ComboBox
   - Added an explicit `ComboBoxItem` dark-theme style.
   - Normal, hover, selected, and disabled text colors are explicitly defined.
   - Selected/hover states use the application's red accent so dropdown text remains readable.

## Explicitly unchanged
- OAuth / auto-login / token refresh
- Twitch chat sending
- Scheduler / hourly message timing
- Event Engine logic
- Channel persistence

## Validation
- All XAML files were parsed successfully as XML in the build environment.
- A full WPF MSBuild was not available in the build environment, so final verification should be performed with Visual Studio on Windows.
