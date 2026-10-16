param(
    [string]$ConfigPath = "$env:LOCALAPPDATA\SmartHdrManagerSafe\games.json",
    [string]$AppPath = "$env:LOCALAPPDATA\Programs\SmartHdrManagerSafe\SmartHdrManager.exe",
    [string[]]$Roots = @(),
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$old = if (Test-Path -LiteralPath $ConfigPath) { Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json } else { $null }
if (!$Roots.Count -and $old) { $Roots = @($old.Roots) }
$entries = [Collections.Generic.List[object]]::new()
$excluded = [Collections.Generic.List[object]]::new()
$folders = @{}
$shell = New-Object -ComObject WScript.Shell
$links = @(foreach ($root in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('StartMenu'),[Environment]::GetFolderPath('CommonStartMenu'))) {
    Get-ChildItem -LiteralPath $root -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | Where-Object FullName -NotMatch '\\Games Library\\' | ForEach-Object {
        $s = $shell.CreateShortcut($_.FullName)
        if ($s.TargetPath -and (Test-Path -LiteralPath $s.TargetPath) -and $_.BaseName -notmatch 'uninstall|bonus|artbook|launcher|shadPS4$') {
            [pscustomobject]@{ Name=$_.BaseName; Target=$s.TargetPath; Arguments=$s.Arguments; WorkingDirectory=$s.WorkingDirectory; Icon=$s.IconLocation }
        }
    }
})
function Get-GameExes([string]$Folder) {
    $queue = [Collections.Generic.Queue[object]]::new(); $queue.Enqueue(@($Folder,0)); $visited=0
    while ($queue.Count -and $visited -lt 1500) {
        $node=$queue.Dequeue(); $visited++
        foreach ($f in Get-ChildItem -LiteralPath $node[0] -File -Filter *.exe -ErrorAction SilentlyContinue) {
            if ($f.Name -notmatch 'unins|setup|install|redist|crashreport|crashhandler|crashpad|report|helper|update|anti.?cheat|battleye|beservice|eac_|dxweb|vc_redist|quicksfv|python|notification|browser|server|artbook|digitalextras|dxdiag|unitycrash|zsync|7z|start_protected|bugsplat|bssndrpt|geck|creationkit|forzaprotocol') { $f.FullName }
        }
        if ($node[1] -ge 5) { continue }
        foreach ($dir in Get-ChildItem -LiteralPath $node[0] -Directory -ErrorAction SilentlyContinue) {
            if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $dir.Name -match '^(_?redist|support|engine|extras|plugins|mods|cache|artbook|digitalextras|tools|nodvd|installers|Papyrus Compiler|node_modules|\.git|__installer)$') { continue }
            $queue.Enqueue(@($dir.FullName,($node[1]+1)))
        }
    }
}
function Add-Game([string]$Name,[string]$Id,[string]$Source,[string]$Folder,[string]$Launch='',[string]$Arguments='') {
    if ($folders.ContainsKey($Folder.ToLowerInvariant())) { return }
    $folders[$Folder.ToLowerInvariant()]=$true
    $exes = @(Get-GameExes $Folder | Sort-Object -Unique)
    if (!$exes.Count -and $Source -eq 'Folder') { $excluded.Add([pscustomobject]@{Folder=$Folder;Reason='No game executable in bounded scan; archive, empty or unresolved'}); return }
    $matches = @($links | Where-Object { $_.Target.StartsWith($Folder.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) })
    $link = $matches | Select-Object -First 1
    $working=$Folder; $icon=''; $notes=''
    if (!$Launch -and $link) { $Launch=$link.Target; $Arguments=$link.Arguments; $working=$link.WorkingDirectory; $icon=$link.Icon }
    if (!$Launch) {
        $top=@($exes | Where-Object { (Split-Path -Parent $_) -eq $Folder -and (Split-Path -Leaf $_) -notmatch 'launcher|config|benchmark|tool|editor|unreal|debug|qt' })
        $shipping=@($exes | Where-Object { $_ -match 'Shipping\.exe$' })
        if ($top.Count -eq 1) { $Launch=$top[0] }
        elseif ($shipping.Count -eq 1) { $Launch=$shipping[0] }
        elseif ($exes.Count -eq 1) { $Launch=$exes[0] }
        else { $notes='Ambiguous launch executable: select Launch in policies before using icon.' }
    }
    $real=@($exes | Where-Object { (Split-Path -Leaf $_) -notmatch 'launcher|config|benchmark|tool|editor|qtlauncher|PlayGTAV' })
    if (!$icon -and $real.Count) { $icon=$real[0]+',0' }
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Id))).Substring(0,20).ToLowerInvariant()
    $g=[pscustomobject]@{Id=$hash;Name=$Name;Source=$Source+':'+$Id;InstallPath=$Folder;Launch=$Launch;Arguments=$Arguments;WorkingDirectory=$working;Icon=$icon;Executables=@($real);Policy='Unknown';Confidence='Unverified: no HDR transition';Display=$(if($old){$old.ManagedDisplay}else{''});Enabled=$true;MoveToDisplay=$true;Notes=$notes}
    $previous=@($old.Games | Where-Object Id -eq $hash) | Select-Object -First 1
    if ($previous) { foreach($prop in @('Policy','Confidence','Display','Enabled','Launch','Arguments','Notes','MoveToDisplay')) { if($previous.PSObject.Properties.Name -contains $prop){ $g.$prop=$previous.$prop } } }
    $entries.Add($g)
}
$steam=Get-ItemPropertyValue 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue
if (!$steam) { $steam="${env:ProgramFiles(x86)}\Steam" }
$libraries=@($steam)
$vdf=Join-Path $steam 'steamapps\libraryfolders.vdf'
if (Test-Path -LiteralPath $vdf) { foreach($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw),'"path"\s+"(.+?)"')) { $libraries+=$m.Groups[1].Value.Replace('\\','\') } }
foreach($lib in ($libraries | Sort-Object -Unique)) {
    foreach($acf in Get-ChildItem -LiteralPath (Join-Path $lib 'steamapps') -Filter appmanifest_*.acf -ErrorAction SilentlyContinue) {
        $text=Get-Content -LiteralPath $acf.FullName -Raw
        $id=[regex]::Match($text,'"appid"\s+"(.*?)"').Groups[1].Value
        $name=[regex]::Match($text,'"name"\s+"(.*?)"').Groups[1].Value
        $dir=[regex]::Match($text,'"installdir"\s+"(.*?)"').Groups[1].Value
        if($id -in @('228980','250820','431960') -or !$dir) { continue }
        $folder=Join-Path $lib "steamapps\common\$dir"
        if(Test-Path -LiteralPath $folder) { Add-Game $name "steam:$id" 'Steam' $folder "steam://rungameid/$id" }
    }
    # Installed folders with no manifest remain explicitly unresolved.
    foreach($dir in Get-ChildItem -LiteralPath (Join-Path $lib 'steamapps\common') -Directory -ErrorAction SilentlyContinue) {
        if($dir.Name -notmatch 'Steamworks|wallpaper_engine|Steam Controller') { Add-Game $dir.Name $dir.FullName 'Folder' $dir.FullName }
    }
}
foreach($item in Get-ChildItem "$env:ProgramData\Epic\EpicGamesLauncher\Data\Manifests" -Filter *.item -ErrorAction SilentlyContinue) {
    $m=Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json
    if (!$m.bIsIncompleteInstall -and (Test-Path -LiteralPath $m.InstallLocation)) { Add-Game $m.DisplayName "epic:$($m.AppName)" 'Epic' $m.InstallLocation "com.epicgames.launcher://apps/$($m.AppName)?action=launch&silent=true" }
}
foreach($key in Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games','HKLM:\SOFTWARE\GOG.com\Games' -ErrorAction SilentlyContinue) {
    $g=Get-ItemProperty $key.PSPath
    if($g.path -and (Test-Path -LiteralPath $g.path)) { Add-Game $g.gameName "gog:$($key.PSChildName)" 'GOG' $g.path $g.exe $g.launchParam }
}
foreach($root in $Roots) { foreach($dir in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) { Add-Game $dir.Name $dir.FullName 'Folder' $dir.FullName } }
# Registered EA / Ubisoft / Battle.net / standalone installations, without scanning whole drives.
foreach($key in Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue) {
    $r=Get-ItemProperty $key.PSPath
    if($r.InstallLocation -and $r.Publisher -match 'Ubisoft|Electronic Arts|Blizzard' -and $r.DisplayName -notmatch 'launcher|connect|Battle.net|EA app' -and (Test-Path -LiteralPath $r.InstallLocation)) { Add-Game $r.DisplayName $key.PSChildName 'Registry' $r.InstallLocation }
}
$result=[pscustomobject]@{ManagedDisplay=$(if($old){$old.ManagedDisplay}else{''});Roots=$Roots;StreamingLog=$(if($old){$old.StreamingLog}else{''});Games=@($entries | Sort-Object Name);AllowPhysicalWhileStreamingHostRuns=$(if($old){$old.AllowPhysicalWhileStreamingHostRuns}else{$false})}
if($DryRun) { $result | ConvertTo-Json -Depth 10; return }
$data=Split-Path -Parent $ConfigPath; New-Item -ItemType Directory -Force $data | Out-Null
if(Test-Path -LiteralPath $ConfigPath) { Copy-Item -LiteralPath $ConfigPath -Destination "$ConfigPath.bak" -Force }
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$ConfigPath.tmp" -Encoding utf8
Move-Item -LiteralPath "$ConfigPath.tmp" -Destination $ConfigPath -Force
@($excluded) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $data 'excluded-folders.json')
$library=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Games Library'
New-Item -ItemType Directory -Force $library | Out-Null
$created=[Collections.Generic.List[string]]::new()
foreach($g in $entries) {
    if(!$g.Launch) { continue }
    $name=($g.Name -replace '[<>:"/\\|?*]','_')
    $same=@($entries | Where-Object Name -eq $g.Name)
    if($same.Count -gt 1) { $name += ' ['+$g.Id.Substring(0,6)+']' }
    $file=Join-Path $library "$name.lnk"
    if(Test-Path -LiteralPath $file) {
        $existing=$shell.CreateShortcut($file)
        if($existing.TargetPath -ne $AppPath) { $file=Join-Path $library "$name (HDR Manager).lnk" }
    }
    $s=$shell.CreateShortcut($file); $s.TargetPath=$AppPath; $s.Arguments='--launch '+$g.Id
    $s.WorkingDirectory=Split-Path -Parent $AppPath; $s.Description='Smart HDR Manager game '+$g.Id
    if($g.Icon) { $s.IconLocation=$g.Icon }; $s.Save(); $created.Add($file)
}
@($created) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'created-shortcuts.json')
Write-Output "Discovered $($entries.Count) entries; created $($created.Count) launch icons. Excluded $($excluded.Count) archive/empty folders."
