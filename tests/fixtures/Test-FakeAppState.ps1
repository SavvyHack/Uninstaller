<#
.SYNOPSIS
  Reports which parts of the Install-FakeApp.ps1 footprint currently exist.
  -Expect Removed : every test-app leftover must be gone and the decoy must be intact.
  -Expect Present : every test-app leftover (except the main exe and its Uninstall key) must exist again.
#>
param([ValidateSet('Removed', 'Present')] [string]$Expect = 'Removed')
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
$app = Join-Path $programs 'Contoso Widget Studio'
$checks = [ordered]@{
    'Install folder'        = { Test-Path $app }
    'Roaming data'          = { Test-Path "$env:APPDATA\Contoso Labs\Widget Studio" }
    'Local cache'           = { Test-Path "$env:LOCALAPPDATA\ContosoWidgetStudio" }
    'Settings key'          = { Test-Path 'HKCU:\Software\Contoso Labs\Widget Studio' }
    'Run entry'             = { $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ContosoWidgetStudio -ErrorAction SilentlyContinue) }
    'App Paths'             = { Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\widgetstudio.exe' }
    'ProgID'                = { Test-Path 'HKCU:\Software\Classes\ContosoWidget.Document' }
    'Scheduled task'        = { $null -ne (Get-ScheduledTask -TaskName ContosoWidgetStudioUpdate -ErrorAction SilentlyContinue) }
    'Start menu folder'     = { Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Contoso Widget Studio" }
    'Desktop shortcut'      = { Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Widget Studio.lnk') }
    'PATH entry'            = { ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') -contains $app }
}
$decoy = [ordered]@{
    'Decoy install folder'  = { Test-Path "$programs\Contoso Notes" }
    'Decoy registration'    = { Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ContosoNotes' }
    'Decoy settings key'    = { Test-Path 'HKCU:\Software\Contoso Labs\Notes' }
    'Decoy data'            = { Test-Path "$env:APPDATA\Contoso Labs\Notes\notes.db" }
}
$fail = 0
foreach ($k in $checks.Keys) {
    $exists = & $checks[$k]
    $ok = if ($Expect -eq 'Removed') { -not $exists } else { $exists }
    if (-not $ok) { $fail++ }
    '{0,-22} {1,-8} {2}' -f $k, $(if ($exists) { 'present' } else { 'gone' }), $(if ($ok) { 'OK' } else { 'FAIL' })
}
foreach ($k in $decoy.Keys) {
    $exists = & $decoy[$k]
    if (-not $exists) { $fail++ }
    '{0,-22} {1,-8} {2}' -f $k, $(if ($exists) { 'present' } else { 'gone' }), $(if ($exists) { 'OK' } else { 'FAIL' })
}
"`n$fail failure(s)"
exit $fail
