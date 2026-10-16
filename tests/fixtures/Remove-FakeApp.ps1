<#
.SYNOPSIS
  Removes everything Install-FakeApp.ps1 planted (both the test app and the decoy). Safe to run repeatedly.
#>
$ErrorActionPreference = 'SilentlyContinue'
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
Remove-Item -Recurse -Force "$programs\Contoso Widget Studio", "$programs\Contoso Notes"
Remove-Item -Recurse -Force "$env:APPDATA\Contoso Labs", "$env:LOCALAPPDATA\ContosoWidgetStudio"
Remove-Item -Recurse -Force "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Contoso Widget Studio"
Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Widget Studio.lnk')
Remove-Item -Recurse -Force 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoWidgetStudio',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoNotes',
    'HKCU:\Software\Contoso Labs',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\widgetstudio.exe',
    'HKCU:\Software\Classes\.cwidget',
    'HKCU:\Software\Classes\ContosoWidget.Document'
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' ContosoWidgetStudio
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' ContosoWidgetStudio
schtasks.exe /Delete /TN 'ContosoWidgetStudioUpdate' /F 2>$null | Out-Null
$app = Join-Path $programs 'Contoso Widget Studio'
$path = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', (($path -split ';' | Where-Object { $_ -and $_ -ne $app }) -join ';'), 'User')
Write-Host 'Fixture removed.'
