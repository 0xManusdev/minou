# Removes NotchAgent: hooks first (dead hook paths would error on every Claude Code event), then the app and its data.
$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\NotchAgent'
$exe = Join-Path $installDir 'NotchAgent.exe'

if (Test-Path $exe) {
    $p = Start-Process -FilePath $exe -ArgumentList '--uninstall-hooks' -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "Impossible de retirer les hooks : l'application n'a pas été supprimée." }
    & $exe --quit
    Start-Sleep -Milliseconds 900
}
Get-Process NotchAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'NotchAgent') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'NotchAgent.lnk') -Force -ErrorAction SilentlyContinue
Write-Host "NotchAgent désinstallé." -ForegroundColor Green
