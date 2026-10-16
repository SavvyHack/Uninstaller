<#
.SYNOPSIS
  Plants a realistic per-user program footprint for end-to-end leftover tests (no admin rights needed).

  "Contoso Widget Studio" gets an install folder, app data, settings keys, a Run entry, a scheduled task,
  a file association, App Paths, Start menu + desktop shortcuts and a PATH entry. Its uninstaller removes
  only the main exe and its registration, leaving everything else behind (a typical sloppy uninstaller).

  "Contoso Notes" is a decoy from the same publisher that must never be reported as a leftover.

  Remove everything again with Remove-FakeApp.ps1.
#>
$ErrorActionPreference = 'Stop'
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
$app = Join-Path $programs 'Contoso Widget Studio'
$exe = Join-Path $app 'widgetstudio.exe'
New-Item -ItemType Directory -Force "$app\plugins" | Out-Null
Copy-Item "$env:WINDIR\System32\where.exe" $exe
Copy-Item "$env:WINDIR\System32\where.exe" "$app\widgetupdater.exe"
Set-Content "$app\plugins\export.dll" ('x' * 2048)
Set-Content "$app\readme.txt" 'Contoso Widget Studio'
@"
@echo off
rem Sloppy uninstaller: removes the main program and its registration, nothing else.
del /f /q "$exe"
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoWidgetStudio" /f >nul
exit /b 0
"@ | Set-Content "$app\uninstall.cmd" -Encoding ASCII

# Registration
$un = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoWidgetStudio'
New-Item $un -Force | Out-Null
Set-ItemProperty $un DisplayName 'Contoso Widget Studio'
Set-ItemProperty $un Publisher 'Contoso Labs'
Set-ItemProperty $un DisplayVersion '3.2.1'
Set-ItemProperty $un InstallLocation $app
Set-ItemProperty $un DisplayIcon "$exe,0"
Set-ItemProperty $un UninstallString "`"$app\uninstall.cmd`""
Set-ItemProperty $un QuietUninstallString "`"$app\uninstall.cmd`""
Set-ItemProperty $un InstallDate (Get-Date -Format yyyyMMdd)

# App data
New-Item -ItemType Directory -Force "$env:APPDATA\Contoso Labs\Widget Studio" | Out-Null
Set-Content "$env:APPDATA\Contoso Labs\Widget Studio\settings.json" '{"theme":"dark"}'
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\ContosoWidgetStudio\cache" | Out-Null
Set-Content "$env:LOCALAPPDATA\ContosoWidgetStudio\cache\thumbs.bin" ('y' * 65536)

# Settings keys
New-Item 'HKCU:\Software\Contoso Labs\Widget Studio\Recent' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\Contoso Labs\Widget Studio' InstallDir $app
Set-ItemProperty 'HKCU:\Software\Contoso Labs\Widget Studio\Recent' File1 'C:\Temp\drawing.cwidget'

# Startup entry, App Paths, file association
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' ContosoWidgetStudio "`"$exe`" --tray"
New-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\widgetstudio.exe' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\widgetstudio.exe' '(default)' $exe
New-Item 'HKCU:\Software\Classes\.cwidget\OpenWithProgids' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\Classes\.cwidget' '(default)' 'ContosoWidget.Document'
New-ItemProperty 'HKCU:\Software\Classes\.cwidget\OpenWithProgids' 'ContosoWidget.Document' -Value '' -PropertyType String -Force | Out-Null
New-Item 'HKCU:\Software\Classes\ContosoWidget.Document\shell\open\command' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\Classes\ContosoWidget.Document' '(default)' 'Contoso Widget'
Set-ItemProperty 'HKCU:\Software\Classes\ContosoWidget.Document\shell\open\command' '(default)' "`"$exe`" `"%1`""

# Scheduled task (current user, no elevation)
$action = New-ScheduledTaskAction -Execute "$app\widgetupdater.exe" -Argument '/check'
Register-ScheduledTask -TaskName 'ContosoWidgetStudioUpdate' -Action $action -Trigger (New-ScheduledTaskTrigger -Daily -At 9am) -Force | Out-Null

# Shortcuts
$shell = New-Object -ComObject WScript.Shell
$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Contoso Widget Studio'
New-Item -ItemType Directory -Force $menu | Out-Null
$lnk = $shell.CreateShortcut("$menu\Widget Studio.lnk"); $lnk.TargetPath = $exe; $lnk.Save()
$desk = [Environment]::GetFolderPath('Desktop')
$lnk = $shell.CreateShortcut("$desk\Widget Studio.lnk"); $lnk.TargetPath = $exe; $lnk.Save()

# PATH entry (user)
$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($path -notlike "*$app*") { [Environment]::SetEnvironmentVariable('Path', ($path.TrimEnd(';') + ";$app"), 'User') }

# ---------------- decoy from the same publisher (must survive) ----------------
$notes = Join-Path $programs 'Contoso Notes'
New-Item -ItemType Directory -Force $notes | Out-Null
Copy-Item "$env:WINDIR\System32\where.exe" "$notes\contosonotes.exe"
$dn = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoNotes'
New-Item $dn -Force | Out-Null
Set-ItemProperty $dn DisplayName 'Contoso Notes'
Set-ItemProperty $dn Publisher 'Contoso Labs'
Set-ItemProperty $dn InstallLocation $notes
Set-ItemProperty $dn UninstallString "`"$notes\contosonotes.exe`" /uninstall"
New-Item 'HKCU:\Software\Contoso Labs\Notes' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\Contoso Labs\Notes' Version '1.0'
New-Item -ItemType Directory -Force "$env:APPDATA\Contoso Labs\Notes" | Out-Null
Set-Content "$env:APPDATA\Contoso Labs\Notes\notes.db" 'my notes'

Write-Host "Planted Contoso Widget Studio (+ decoy Contoso Notes)."
