$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'src\TwitchHourlyBot.Api\appsettings.Local.json'

function Save-Config($id, $secret) {
    @{ Twitch = @{ ClientId = $id; ClientSecret = $secret } } | ConvertTo-Json | Set-Content -Path $target -Encoding UTF8
    Write-Host "Saved to $target (this file is git-ignored)."
}

# 1) Reuse the credentials the desktop app already saved for this Windows user (DPAPI-protected, never leaves this PC).
try {
    $dir = Join-Path $env:LOCALAPPDATA 'TwitchHourlyBot'
    $settingsPath = Join-Path $dir 'settings.json'
    $secretPath = Join-Path $dir 'client-secret.dat'
    if ((Test-Path $settingsPath) -and (Test-Path $secretPath)) {
        $id = ((Get-Content $settingsPath -Raw | ConvertFrom-Json).ClientId + '').Trim()
        Add-Type -AssemblyName System.Security
        $bytes = [Convert]::FromBase64String((Get-Content $secretPath -Raw).Trim())
        $plain = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, 'CurrentUser')).Trim()
        if ($id -and $plain) {
            Write-Host "Found a Twitch application saved by the desktop app: Client ID $id"
            Write-Host 'The web login only works if THIS application lists http://localhost:4200/api/auth/callback under OAuth Redirect URLs.'
            $answer = (Read-Host 'Use it? [Y = yes, N = enter a different Client ID/Secret]').Trim()
            if ($answer -eq '' -or $answer -match '^[Yy]') {
                Save-Config $id $plain
                exit 0
            }
        }
    }
} catch {
    Write-Host 'Could not reuse the desktop app credentials; asking instead.'
}

# 2) Otherwise ask once.
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
Save-Config $id $secret
