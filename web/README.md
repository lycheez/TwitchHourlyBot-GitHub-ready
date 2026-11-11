# Dashboard (Angular)

Web UI for the Twitch Hourly Bot API: manage channels and keyword rules, and try a chat message against a channel's rules.

## Run locally

1. Start the API: `dotnet run --project src/TwitchHourlyBot.Api` (from the repository root). It listens on `http://localhost:5000` by default; change the target in `proxy.conf.json` if yours differs.
2. In this folder: `npm install`, then `npm start` and open http://localhost:4200. Requests to `/api` are proxied to the API.

## Build and test

```
npm run build
npm test -- --watch=false
```
