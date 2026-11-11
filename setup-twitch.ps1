$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'src\TwitchHourlyBot.Api\appsettings.Local.json'

Write-Host 'Create a Twitch application at https://dev.twitch.tv/console and add this OAuth Redirect URL:'
Write-Host '  http://localhost:4200/api/auth/callback'
Write-Host ''
$id = (Read-Host 'Client ID').Trim()
$secure = Read-Host 'Client Secret' -AsSecureString
$secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)).Trim()

if (-not $id -or -not $secret) {
    Write-Host 'Both values are required.'
    exit 1
}

@{ Twitch = @{ ClientId = $id; ClientSecret = $secret } } | ConvertTo-Json | Set-Content -Path $target -Encoding UTF8
Write-Host "Saved to $target (this file is git-ignored)."
