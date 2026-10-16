# SonsVR Diagnostic Collector v2 -- requires Windows PowerShell 5.1+.
# Read-only collection. No game hooks, admin rights, registry edits, or cloud access.
# Run via COLLECT_SONSVR_LOGS.bat. A valid game path may be given as its first argument.
# All zip files are written next to this .ps1, never to an inferred user path.
param([string]$GameDirectory = '')

$ErrorActionPreference = 'Stop'
$maxFileBytes = 50MB
$maxTotalBytes = 250MB
$bytesCopied = [long]0
$numberCopied = 0
$manifest = New-Object 'System.Collections.Generic.List[string]'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$zipPath = Join-Path -Path $PSScriptRoot -ChildPath ("SONSVR_DIAGNOSTICS_$stamp.zip")
$stage = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("SONSVR_stage_$([guid]::NewGuid().ToString('N'))")

function Resolve-GameFolder {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    # Trim paired/unpaired double-quotes from pasted Explorer paths; quotes are
    # illegal within a Windows filename. Preserve apostrophes, brackets, &, !, etc.
    $v = $Value.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($v)) { return $null }
    try {
        $item = Get-Item -LiteralPath $v -ErrorAction Stop
        if ($item -is [System.IO.FileInfo]) {
            if ($item.Extension -ine '.exe') { return $null }
            $item = $item.Directory
        }
        if ($item.Name -ieq 'MelonLoader') { $item = $item.Parent }
        if ($null -eq $item) { return $null }
        $exe = Join-Path $item.FullName 'SonsOfTheForest.exe'
        $melon = Join-Path $item.FullName 'MelonLoader'
        if ((Test-Path -LiteralPath $exe -PathType Leaf) -or
            (Test-Path -LiteralPath $melon -PathType Container)) {
            return $item.FullName
        }
    } catch { }
    return $null
}

function Find-GameFolder {
    # Auto-detection is a convenience, never needed for a custom Steam Library.
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $candidates.Add($PSScriptRoot)
    $candidates.Add((Split-Path -Parent $PSScriptRoot))
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Sons Of The Forest'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Steam\steamapps\common\Sons Of The Forest'))
    }
    foreach ($candidate in $candidates) {
        $resolved = Resolve-GameFolder $candidate
        if ($null -ne $resolved) { return $resolved }
    }
    return $null
}

function Select-GameFolder {
    param([string]$Requested)
    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        $resolved = Resolve-GameFolder $Requested
        if ($null -ne $resolved) { return $resolved }
        Write-Host 'The supplied folder was not recognized as a Sons Of The Forest installation.' -ForegroundColor Yellow
    }
    $auto = Find-GameFolder
    Write-Host ''
    Write-Host 'SonsVR diagnostic collector' -ForegroundColor Cyan
    Write-Host 'Paste the GAME INSTALL folder containing SonsOfTheForest.exe.'
    Write-Host 'Example: D:\SteamLibrary\steamapps\common\Sons Of The Forest'
    Write-Host 'You may also drag the folder into this window.'
    if ($null -ne $auto) {
        Write-Host "Detected: $auto"
        Write-Host 'Press ENTER to accept it.'
    } else {
        Write-Host 'Press ENTER without a path to collect user-data logs only (game logs omitted).'
    }
    while ($true) {
        $typed = Read-Host 'Game folder'
        if ([string]::IsNullOrWhiteSpace($typed)) {
            if ($null -eq $auto) {
                Write-Host 'WARNING: Game folder skipped. F9 snapshots can still be collected.' -ForegroundColor Yellow
            }
            return $auto
        }
        $resolved = Resolve-GameFolder $typed
        if ($null -ne $resolved) { return $resolved }
        Write-Host 'Not a recognized game folder. Paste the installation folder, not its Mods folder.' -ForegroundColor Yellow
    }
}

function Collect-One {
    param([string]$SourceFile, [string]$Category)
    try {
        if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) { return }
        $fi = Get-Item -LiteralPath $SourceFile -ErrorAction Stop
        if ($fi.Length -gt $script:maxFileBytes) {
            $script:manifest.Add("SKIPPED (>50 MB): $($fi.FullName)")
            return
        }
        if (($script:bytesCopied + $fi.Length) -gt $script:maxTotalBytes) {
            $script:manifest.Add("SKIPPED (250 MB collection limit): $($fi.FullName)")
            return
        }
        $destDir = Join-Path $script:stage $Category
        if (-not (Test-Path -LiteralPath $destDir -PathType Container)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        $dest = Join-Path $destDir $fi.Name
        # The game may still be writing a log; copy failures are nonfatal.
        Copy-Item -LiteralPath $fi.FullName -Destination $dest -Force -ErrorAction Stop
        $script:numberCopied += 1
        $script:bytesCopied += $fi.Length
        $script:manifest.Add(('{0} | {1:yyyy-MM-dd HH:mm:ss.fff} | {2} bytes | {3}' -f
            $Category, $fi.LastWriteTime, $fi.Length, $fi.FullName))
    } catch {
        $script:manifest.Add("COPY FAILED: $SourceFile | $($_.Exception.Message)")
    }
}

function Collect-Recent {
    param([string]$Folder, [string]$Filter, [int]$Limit, [string]$Category)
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) {
        $script:manifest.Add("MISSING DIRECTORY: $Folder")
        return
    }
    try {
        $recent = @(Get-ChildItem -LiteralPath $Folder -File -Filter $Filter -ErrorAction Stop |
            Sort-Object LastWriteTime -Descending | Select-Object -First $Limit)
        foreach ($file in $recent) { Collect-One -SourceFile $file.FullName -Category $Category }
    } catch {
        $script:manifest.Add("LIST FAILED: $Folder ($Filter) | $($_.Exception.Message)")
    }
}

$exitCode = 0
try {
    $requested = $GameDirectory
    if ([string]::IsNullOrWhiteSpace($requested)) { $requested = $env:SONSVR_COLLECT_GAME_DIR }
    if ([string]::IsNullOrWhiteSpace($requested)) { $requested = $env:SONS_GAME_DIR }
    $gameRoot = Select-GameFolder -Requested $requested

    # Unity's Application.persistentDataPath normally resolves to LocalLow.
    # Use the Windows known-folder API, not %APPDATA% (which is Roaming).
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'Windows LocalApplicationData known-folder location is unavailable.'
    }
    $localLow = Join-Path (Split-Path -Parent $localAppData) 'LocalLow'
    $gameData = Join-Path $localLow 'Endnight\SonsOfTheForest'
    # Support an alternative Unity company/product folder spelling if encountered.
    if (-not (Test-Path -LiteralPath $gameData -PathType Container)) {
        $alternative = Join-Path $localLow 'Endnight\Sons Of The Forest'
        if (Test-Path -LiteralPath $alternative -PathType Container) { $gameData = $alternative }
    }
    $diagRoot = Join-Path $gameData 'SonsVR_Diagnostics'

    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $manifest.Add('SONSVR Collector v2')
    $manifest.Add('Collection local time: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff zzz'))
    $manifest.Add('Game folder: ' + $(if ($null -eq $gameRoot) { '(not supplied)' } else { $gameRoot }))
    $manifest.Add('Unity user-data folder: ' + $gameData)
    $manifest.Add('Collection policy: selected SonsVR logs and F9 snapshots; NO saves, images or crash dumps.')
    $manifest.Add('Review logs for personal paths/identifiers before sharing publicly.')
    $manifest.Add('')

    # This is where V169B/V169C F9 snapshots were written (NOT the game folder).
    Collect-Recent $diagRoot '*.txt' 50 'F9_Snapshots'
    Collect-Recent $diagRoot '*.csv' 10 'F9_Diagnostics'
    Collect-Recent $diagRoot '*.tsv' 10 'F9_Diagnostics'
    Collect-Recent $diagRoot '*.log' 10 'F9_Diagnostics'

    # Unity user logs and rare telemetry written under LocalLow.
    Collect-One (Join-Path $gameData 'Player.log') 'Unity'
    Collect-Recent $gameData 'SonsVR_*.log' 12 'Unity_Telemetry'
    Collect-Recent $gameData 'SonsVR_*.csv' 12 'Unity_Telemetry'
    Collect-Recent $gameData 'SonsVR_*.tsv' 12 'Unity_Telemetry'
    Collect-Recent $gameData 'SonsVR_*.txt' 12 'Unity_Telemetry'

    if ($null -ne $gameRoot) {
        $melonRoot = Join-Path $gameRoot 'MelonLoader'
        Collect-One (Join-Path $melonRoot 'Latest.log') 'MelonLoader'
        Collect-Recent (Join-Path $melonRoot 'Logs') '*.log' 5 'MelonLoader_OldLogs'

        # IMPORTANT: our V145/V160/Perf/etc. traces are written to GAME/Mods.
        # The original v1 collector missed them.
        $mods = Join-Path $gameRoot 'Mods'
        Collect-Recent $mods 'SonsVR_*.log' 12 'Mods_Traces'
        Collect-Recent $mods 'SonsVR_*.csv' 20 'Mods_Traces'
        Collect-Recent $mods 'SonsVR_*.tsv' 20 'Mods_Traces'
        Collect-Recent $mods 'SonsVR_*.txt' 12 'Mods_Traces'
    }
    $manifest | Set-Content -LiteralPath (Join-Path $stage 'MANIFEST.txt') -Encoding UTF8
    if ($numberCopied -eq 0) {
        throw 'No matching diagnostics found. Check the chosen game directory, Windows account, and F9 output path.'
    }

    # Avoid wildcard/quoted path argument parsing entirely, including spaces, [], !, &, and trailing \.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host ''
    Write-Host "SUCCESS: $zipPath" -ForegroundColor Green
    Write-Host "Files: $numberCopied; uncompressed bytes: $bytesCopied"
    if ($null -eq $gameRoot) {
        Write-Host 'WARNING: Game MelonLoader and Mods logs were skipped (no game path).' -ForegroundColor Yellow
    }
    if (-not (Test-Path -LiteralPath $diagRoot -PathType Container)) {
        Write-Host "WARNING: No F9 snapshot folder found at: $diagRoot" -ForegroundColor Yellow
    }
    Write-Host 'Upload that ZIP; include incident times and a short note.'
} catch {
    Write-Host ''
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    $exitCode = 1
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
