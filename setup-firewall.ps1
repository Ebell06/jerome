# setup-firewall.ps1
# Adds a real Windows Firewall restriction for JeromeSharp.exe.
#
# HONEST LIMITATION: Windows' built-in firewall filters by IP/port, not by
# domain name. It cannot do "allow elevenlabs.io, block everything else" the
# way Fletcher's DNS filtering does — that needs a local proxy or third-party
# tool this script doesn't set up. What this DOES give you: JeromeSharp.exe
# is blocked from making outbound connections on any port except 443 (HTTPS)
# and 53 (DNS) — a real, if coarse, boundary. If Jerome's code ever tried to
# open a raw socket, an unexpected port, or anything other than normal HTTPS
# calls, this blocks it at the OS level regardless of what the C# code does.
#
# Run this once, as Administrator:
#   Right-click PowerShell -> "Run as administrator", then:
#   Set-ExecutionPolicy -Scope Process Bypass -Force
#   .\setup-firewall.ps1

$exePath = Join-Path $PSScriptRoot "bin\Debug\net10.0-windows\JeromeSharp.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Couldn't find $exePath — build Jerome first (dotnet build), then re-run this script." -ForegroundColor Yellow
    exit 1
}

# Remove any previous rules from earlier runs, so re-running this script is safe.
Get-NetFirewallRule -DisplayName "Jerome*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Block all outbound traffic from JeromeSharp.exe by default.
New-NetFirewallRule -DisplayName "Jerome - Block All Outbound" `
    -Direction Outbound -Program $exePath -Action Block -Profile Any | Out-Null

# Explicitly allow HTTPS (443) — needed for ElevenLabs, Spotify, and the dashboard APIs.
New-NetFirewallRule -DisplayName "Jerome - Allow HTTPS" `
    -Direction Outbound -Program $exePath -Action Allow -Protocol TCP -RemotePort 443 -Profile Any | Out-Null

# Explicitly allow DNS (53) — needed to resolve those domains in the first place.
New-NetFirewallRule -DisplayName "Jerome - Allow DNS" `
    -Direction Outbound -Program $exePath -Action Allow -Protocol UDP -RemotePort 53 -Profile Any | Out-Null

Write-Host "Done. JeromeSharp.exe can now only reach the network over HTTPS and DNS — nothing else." -ForegroundColor Green
Write-Host "To undo this later: Get-NetFirewallRule -DisplayName 'Jerome*' | Remove-NetFirewallRule" -ForegroundColor Gray
