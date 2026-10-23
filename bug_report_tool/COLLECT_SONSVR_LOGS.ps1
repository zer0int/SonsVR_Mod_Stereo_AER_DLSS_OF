# SonsVR Diagnostic Collector v3 -- requires Windows PowerShell 5.1+.
# Read-only collection. No game hooks, admin rights, registry edits, or cloud access.
# Use COLLECT_SONSVR_LOGS_PRIVATE.bat for privacy-reduced, non-crash reports.
# All zip files are written next to this .ps1, never to an inferred user path.
param([string]$GameDirectory = '', [switch]$PrivacyReduced)

$ErrorActionPreference = 'Stop'
$maxFileBytes = 50MB
$maxTotalBytes = 250MB
$bytesCopied = [long]0
$numberCopied = 0
$manifest = New-Object 'System.Collections.Generic.List[string]'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$zipPrefix = if ($PrivacyReduced) { 'SONSVR_PRIVATE' } else { 'SONSVR_DIAGNOSTICS' }
$zipPath = Join-Path -Path $PSScriptRoot -ChildPath ("${zipPrefix}_$stamp.zip")
# V3: stage beside the ZIP, keeping large temporary output off the system drive.
$stage = Join-Path -Path $PSScriptRoot -ChildPath ("SONSVR_stage_$([guid]::NewGuid().ToString('N'))")

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

# V3 privacy mode is an ALLOWLIST, not a guarantee of anonymity. No source
# log is copied verbatim. Path-bearing, hardware, account and network lines
# are discarded; F9 free-text UI sections are omitted entirely.
function Filter-PrivacyLine {
    param([string]$Line)
    if ([string]::IsNullOrEmpty($Line)) { return $Line }
    if ($Line -match '(?i)(?:[a-z]:[\\/]|\\\\|/Users/|/home/|AppData[\\/])') { return $null }
    if ($Line -match '(?i)\b(?:GPU|VRAM|CPU|Driver|OS|Windows|Hardware|Serial|HostName|ComputerName|UserName|PlayerName|Account|Email|Password|Token|Auth|SteamID|Steam_Id|Steam|IPv4|IPv6|MACAddress|GPUName|GPUModel|DeviceName|SystemInfo|GraphicsDevice|IPAddress|NVIDIA|Radeon)\b') { return $null }
    if ($Line -match '(?i)(?:\b[0-9]{17}\b|\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b)') { return $null }
    if ($Line -match '(?i)(?:\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b|\b(?:[0-9a-f]{1,4}:){3,}[0-9a-f]{0,4}\b|\b[0-9a-f]{2}(?:[:-][0-9a-f]{2}){5}\b|\b\S+@\S+\.\S+\b)') { return $null }
    $textIndex = $Line.IndexOf(' text="', [System.StringComparison]::OrdinalIgnoreCase)
    if ($textIndex -ge 0) { $Line = $Line.Substring(0, $textIndex) + ' text="[REDACTED]"' }
    # Display/render dimensions are not needed for this everyday-bug profile.
    return [regex]::Replace($Line, '(?i)\b\d{3,5}x\d{3,5}\b', '[RESOLUTION]')
}

function Collect-PrivateOne {
    param([string]$SourceFile, [string]$Category, [string]$Kind)
    if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) { return }
    $fi = $null
    $dest = $null
    try {
        $fi = Get-Item -LiteralPath $SourceFile -ErrorAction Stop
        if ($fi.Length -gt $script:maxFileBytes) {
            $script:manifest.Add('SKIPPED oversized: ' + $fi.Name)
            return
        }
        $destDir = Join-Path $script:stage $Category
        if (-not (Test-Path -LiteralPath $destDir -PathType Container)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        $dest = Join-Path $destDir $fi.Name
        $sourceStream = $null
        $reader = $null
        $writer = $null
        try {
            # MelonLoader may still be writing Latest.log while this reads it.
            $sourceStream = [System.IO.FileStream]::new($fi.FullName,
                [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite)
            $reader = [System.IO.StreamReader]::new($sourceStream, [System.Text.Encoding]::UTF8, $true)
            $writer = [System.IO.StreamWriter]::new($dest, $false, [System.Text.UTF8Encoding]::new($false))
            $section = ''
            while ($true) {
                $line = $reader.ReadLine()
                if ($null -eq $line) { break }
                if ($Kind -eq 'mod-log' -and
                    $line -notmatch '\[(?:SonsVR_Mod|VRPlayer|StereoRender|VRSystem|SonsVR|AER|V[0-9]{2,3})\b') {
                    continue
                }
                if ($Kind -eq 'snapshot') {
                    if ($line -match '^=+\s+([A-Z0-9_]+)\s+=+') { $section = $matches[1] }
                    if ($section -ne '' -and $section -notin @('PLAYER','CAMERAS','VOLUMES','CANVASES')) {
                        continue
                    }
                }
                $safe = Filter-PrivacyLine -Line $line
                if ($null -ne $safe) { $writer.WriteLine($safe) }
            }
        } finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $sourceStream) { $sourceStream.Dispose() }
            if ($null -ne $writer) { $writer.Dispose() }
        }
        $written = Get-Item -LiteralPath $dest
        if ($written.Length -eq 0) {
            Remove-Item -LiteralPath $dest -Force
            return
        }
        if (($script:bytesCopied + $written.Length) -gt $script:maxTotalBytes) {
            Remove-Item -LiteralPath $dest -Force
            $script:manifest.Add('SKIPPED collection limit: ' + $fi.Name)
            return
        }
        $script:numberCopied += 1
        $script:bytesCopied += $written.Length
        $script:manifest.Add(('{0} | {1} | {2} bytes | FILTERED' -f
            $Category, $fi.Name, $written.Length))
    } catch {
        # Do not copy failures or exception paths into the shareable manifest.
        $label = if ($null -ne $fi) { $fi.Name } else { '[unreadable file]' }
        $script:manifest.Add('FILTER FAILED (not copied): ' + $label)
        if ($null -ne $dest -and (Test-Path -LiteralPath $dest -PathType Leaf)) {
            Remove-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
        }
    }
}

function Collect-PrivateRecent {
    param([string]$Folder, [string]$Filter, [int]$Limit, [string]$Category, [string]$Kind)
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return }
    try {
        $recent = @(Get-ChildItem -LiteralPath $Folder -File -Filter $Filter -ErrorAction Stop |
            Sort-Object LastWriteTime -Descending | Select-Object -First $Limit)
        foreach ($file in $recent) {
            Collect-PrivateOne -SourceFile $file.FullName -Category $Category -Kind $Kind
        }
    } catch {
        $script:manifest.Add('Could not enumerate: ' + $Category)
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
    $manifest.Add('SONSVR Collector v3')
    $manifest.Add('Collection local time: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff zzz'))
    if ($PrivacyReduced) {
        $manifest.Add('PRIVACY-REDUCED / NON-CRASH BUG REPORT')
        $manifest.Add('Contents: current SonsVR-only MelonLoader lines and up to 3 filtered F9 snapshots.')
        $manifest.Add('OMITTED: Player.log, system startup lines, rotated old logs, Mods traces, UI text, saves, images, crash dumps.')
        $manifest.Add('Common paths and hardware/account/network patterns are removed; game coordinates/object names may remain.')
        $manifest.Add('NOT ANONYMOUS. Inspect all remaining content before sharing publicly.')
        $manifest.Add('')
        Collect-PrivateRecent $diagRoot 'V*_UI_*.txt' 3 'F9_Snapshots' 'snapshot'
        if ($null -ne $gameRoot) {
            Collect-PrivateOne (Join-Path $gameRoot 'MelonLoader\Latest.log') 'MelonLoader' 'mod-log'
        }
    } else {
        # The original v2 full collector remains available for crashes and
        # specialized performance reports; full source paths are intentionally
        # retained only in this explicitly chosen FULL mode.
        $manifest.Add('FULL DIAGNOSTIC MODE (may include personal information)')
        $manifest.Add('Game folder: ' + $(if ($null -eq $gameRoot) { '(not supplied)' } else { $gameRoot }))
        $manifest.Add('Unity user-data folder: ' + $gameData)
        $manifest.Add('Collection policy: selected SonsVR logs and F9 snapshots; NO saves, images or crash dumps.')
        $manifest.Add('Review logs for personal paths/identifiers before sharing publicly.')
        $manifest.Add('')

        Collect-Recent $diagRoot '*.txt' 50 'F9_Snapshots'
        Collect-Recent $diagRoot '*.csv' 10 'F9_Diagnostics'
        Collect-Recent $diagRoot '*.tsv' 10 'F9_Diagnostics'
        Collect-Recent $diagRoot '*.log' 10 'F9_Diagnostics'

        Collect-One (Join-Path $gameData 'Player.log') 'Unity'
        Collect-Recent $gameData 'SonsVR_*.log' 12 'Unity_Telemetry'
        Collect-Recent $gameData 'SonsVR_*.csv' 12 'Unity_Telemetry'
        Collect-Recent $gameData 'SonsVR_*.tsv' 12 'Unity_Telemetry'
        Collect-Recent $gameData 'SonsVR_*.txt' 12 'Unity_Telemetry'

        if ($null -ne $gameRoot) {
            $melonRoot = Join-Path $gameRoot 'MelonLoader'
            Collect-One (Join-Path $melonRoot 'Latest.log') 'MelonLoader'
            Collect-Recent (Join-Path $melonRoot 'Logs') '*.log' 5 'MelonLoader_OldLogs'

            # V145/V160/performance traces are written into GAME/Mods.
            $mods = Join-Path $gameRoot 'Mods'
            Collect-Recent $mods 'SonsVR_*.log' 12 'Mods_Traces'
            Collect-Recent $mods 'SonsVR_*.csv' 20 'Mods_Traces'
            Collect-Recent $mods 'SonsVR_*.tsv' 20 'Mods_Traces'
            Collect-Recent $mods 'SonsVR_*.txt' 12 'Mods_Traces'
        }
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
        if ($PrivacyReduced) {
            Write-Host 'WARNING: Latest.log was skipped (no game path); only F9 snapshots collected.' -ForegroundColor Yellow
        } else {
            Write-Host 'WARNING: Game MelonLoader and Mods logs were skipped (no game path).' -ForegroundColor Yellow
        }
    }
    if (-not (Test-Path -LiteralPath $diagRoot -PathType Container)) {
        Write-Host "WARNING: No F9 snapshot folder found at: $diagRoot" -ForegroundColor Yellow
    }
    if ($PrivacyReduced) {
        Write-Host 'PRIVACY-REDUCED: no Player.log or unfiltered game logs were included.' -ForegroundColor Cyan
        Write-Host 'This ZIP is not guaranteed anonymous. Review it before sharing.' -ForegroundColor Yellow
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
