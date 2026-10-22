# Builds Minou, installs it for the current user and wires the Claude Code hooks.
#   powershell -ExecutionPolicy Bypass -File install.ps1
# Running it again updates in place.
[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Minou'
$exe = Join-Path $installDir 'Minou.exe'

Write-Host "Minou" -ForegroundColor White
Write-Host ""

Write-Host "1. Arrêt de l'instance en cours..."
if (Get-Process Minou -ErrorAction SilentlyContinue) {
    if (Test-Path $exe) { & $exe --quit; Start-Sleep -Milliseconds 900 }
    Get-Process Minou -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

if (-not $NoBuild) {
    Write-Host "2. Compilation..."
    & (Join-Path $root 'build.ps1')
}

Write-Host "3. Installation dans $installDir"
New-Item -ItemType Directory -Force $installDir | Out-Null
# A hook from a running Claude Code session may hold the exe for a few milliseconds: retry briefly.
for ($attempt = 1; ; $attempt++) {
    try { Copy-Item (Join-Path $root 'bin\Minou.exe') $exe -Force; break }
    catch { if ($attempt -ge 20) { throw }; Start-Sleep -Milliseconds 150 }
}

$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Minou.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($menu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $installDir
$lnk.Description = 'Statut de Claude Code dans une encoche en haut de l''écran'
$lnk.Save()

Write-Host "4. Hooks Claude Code (~/.claude/settings.json)..."
$p = Start-Process -FilePath $exe -ArgumentList '--install-hooks' -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0) { throw "La configuration des hooks a échoué." }

# The script-based notch this replaces, if present.
$legacy = Join-Path $env:USERPROFILE '.claude\notch'
if (Test-Path $legacy) {
    $pidFile = Join-Path $legacy 'notch.pid'
    if (Test-Path $pidFile) { Stop-Process -Id ([int](Get-Content $pidFile)) -Force -ErrorAction SilentlyContinue }
    Remove-Item $legacy -Recurse -Force
    Write-Host "   Ancienne encoche (script PowerShell) retirée."
}

Write-Host ""
Write-Host "Terminé. L'encoche apparaîtra au prochain lancement de Claude Code." -ForegroundColor Green
Write-Host "Les sessions déjà ouvertes la font apparaître à leur prochain message."
