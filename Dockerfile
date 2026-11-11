# Builds only the web API and its UI-independent core (the WPF app is Windows-only).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TwitchHourlyBot.Core/TwitchHourlyBot.Core.csproj src/TwitchHourlyBot.Core/
COPY src/TwitchHourlyBot.Api/TwitchHourlyBot.Api.csproj src/TwitchHourlyBot.Api/
RUN dotnet restore src/TwitchHourlyBot.Api/TwitchHourlyBot.Api.csproj
COPY src/TwitchHourlyBot.Core src/TwitchHourlyBot.Core
COPY src/TwitchHourlyBot.Api src/TwitchHourlyBot.Api
RUN dotnet publish src/TwitchHourlyBot.Api/TwitchHourlyBot.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/twitchbot.db"
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "TwitchHourlyBot.Api.dll"]
