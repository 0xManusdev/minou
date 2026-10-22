# Removes Minou: hooks first (dead hook paths would error on every Claude Code event), then the app and its data.
$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Minou'
$exe = Join-Path $installDir 'Minou.exe'

if (Test-Path $exe) {
    $p = Start-Process -FilePath $exe -ArgumentList '--uninstall-hooks' -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "Impossible de retirer les hooks : l'application n'a pas été supprimée." }
    & $exe --quit
    Start-Sleep -Milliseconds 900
}
Get-Process Minou -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Minou') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Minou.lnk') -Force -ErrorAction SilentlyContinue
Write-Host "Minou désinstallé." -ForegroundColor Green
