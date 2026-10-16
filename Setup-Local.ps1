param([string]$ReleaseFolder = "$PSScriptRoot\publish-safe",[switch]$DryRun,[switch]$Uninstall)
$ErrorActionPreference='Stop'
$install=Join-Path $env:LOCALAPPDATA 'Programs\SmartHdrManagerSafe'
$data=Join-Path $env:LOCALAPPDATA 'SmartHdrManagerSafe'
$exe=Join-Path $install 'SmartHdrManager.exe'
$startup=Join-Path ([Environment]::GetFolderPath('Startup')) 'Smart HDR Manager Safe.lnk'
if($DryRun) { [pscustomobject]@{Install=$install;Data=$data;Startup=$startup;Uninstall=[bool]$Uninstall}; return }
if($Uninstall) {
    if(Test-Path -LiteralPath $exe) { & $exe --exit; Start-Sleep -Seconds 3 }
    if(Get-Process SmartHdrManager -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) { throw 'App still running; exit from tray before uninstalling.' }
    $shell=New-Object -ComObject WScript.Shell
    if(Test-Path -LiteralPath $startup) { if($shell.CreateShortcut($startup).TargetPath -eq $exe) { Remove-Item -LiteralPath $startup } }
    $manifest=Join-Path $data 'created-shortcuts.json'
    if(Test-Path -LiteralPath $manifest) { foreach($file in (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)) {
        if(Test-Path -LiteralPath $file) { if($shell.CreateShortcut($file).TargetPath -eq $exe) { Remove-Item -LiteralPath $file } }
    } }
    # Preserve local configuration, journal, backups and binaries for recovery. No recursive deletion.
    Write-Output "Startup and managed launch icons removed. Data and binaries retained at $data and $install."
    return
}
if(!(Test-Path -LiteralPath (Join-Path $ReleaseFolder 'SmartHdrManager.exe'))) { throw 'Publish a self-contained release first.' }
$other=Get-Process SmartHdrManager -ErrorAction SilentlyContinue | Where-Object Path -ne $exe
if($other) { throw 'Another Smart HDR Manager installation is running. Exit it before installing this fork.' }
if(Test-Path -LiteralPath $exe) { & $exe --exit; Start-Sleep -Seconds 3 }
if(Get-Process SmartHdrManager -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) { throw 'App has not exited.' }
New-Item -ItemType Directory -Force $install,$data | Out-Null
if(Test-Path -LiteralPath $exe) { $backup=Join-Path $data ('backup_'+(Get-Date -Format yyyyMMdd_HHmmss)); Copy-Item -LiteralPath $install -Destination $backup -Recurse }
Get-ChildItem -LiteralPath $ReleaseFolder -File | Copy-Item -Destination $install -Force
Copy-Item -LiteralPath "$PSScriptRoot\Discover-Games.ps1","$PSScriptRoot\Setup-Local.ps1","$PSScriptRoot\LICENSE.txt" -Destination $install -Force
$shell=New-Object -ComObject WScript.Shell
$s=$shell.CreateShortcut($startup); $s.TargetPath=$exe; $s.Arguments='--tray'; $s.WorkingDirectory=$install; $s.Save()
Start-Process -FilePath $exe -ArgumentList '--tray' -WindowStyle Hidden
Write-Output "Installed $exe with per-user startup. No administrative rights used."
