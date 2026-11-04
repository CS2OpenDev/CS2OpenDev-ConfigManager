<#
Runs inside the Windows test image. Run once as the standard user "player" (docker run --user player) and once as
ContainerAdministrator (the default). Asserts what the container can prove; for what it may not (SystemParametersInfo,
powercfg, the session-user check, NVAPI), it prints what happened rather than asserting it.
Every write lands in the container's own registry hive, ProgramData and C:\Steam, all thrown away with it. It refuses
to run unless ACB_DISPOSABLE_MACHINE is 1, which only the image sets: on a developer's PC it would write that PC's
registry and the ACL of C:\Steam.
#>
$ErrorActionPreference = 'Stop'
if ($env:ACB_DISPOSABLE_MACHINE -ne '1') {
    throw 'Refusing to run: these scenarios write the registry, ACLs and C:\Steam. ACB_DISPOSABLE_MACHINE=1 is set only in the throwaway test image.'
}
$acb = 'C:\Program Files\acb\acb.exe'
$fixture = 'C:\acb-fixture\New-FakeSteam.ps1'
$work = Join-Path $env:TEMP ("acb-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$script:pass = 0

function Acb([int] $want, [string[]] $arguments) {
    $out = & $acb @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne $want) { Write-Host $out; throw "acb $($arguments -join ' ') exited $LASTEXITCODE; expected $want" }
    $out
}
function Holds([string] $path, [string] $text) {
    $got = (Get-Content -Raw $path).Trim()
    if ($got -ne $text) { throw "$path holds '$got'; expected '$text'" }
}
function Ok([string] $what) { Write-Host "ok: $what"; $script:pass++ }
function Info([string] $what) { Write-Host "info: $what" }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Info "running as $($identity.Name), administrator: $admin, OS $([Environment]::OSVersion.Version)"

$steam = 'C:\Steam'
$autoexec = "$steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\autoexec.cfg"
& $fixture -Root "$work\source" -Autoexec 'echo source' | Out-Null
& $fixture -Root $steam -Autoexec 'echo target' -Launch '-target' -Register | Out-Null
Acb 0 @('backup', '-o', "$work\backup.zip", '--only', 'cs2,cs2game,launch', '--steam-path', "$work\source") | Out-Null
Ok 'backup from a named Steam folder'

if ($admin) {
    # An administrator's acb writes nothing through the Steam folder (design.md, "Elevation (Windows)").
    Acb 2 @('restore', "$work\backup.zip", '--yes') | Out-Null
    Holds $autoexec 'echo target'
    Ok 'as ContainerAdministrator: nothing written through the Steam folder, exit 2'

    # The administrator's store: what acb makes under the container's own ProgramData, and its ACL.
    $out = & $acb undo --list 2>&1 | Out-String
    Info "undo --list as administrator exited $LASTEXITCODE"
    $store = Join-Path $env:ProgramData 'CS2OpenDev\ConfigBackup\undo'
    if (Test-Path $store) { Info ("store ACL: " + ((Get-Acl $store).Access | ForEach-Object { "$($_.IdentityReference):$($_.FileSystemRights)" }) -join '; ') }
    else { Info 'no administrator store made (expected while no administrator section applies: no NVIDIA here)' }
}
else {
    # 1. SteamPath in HKCU found by acb itself; restore and undo with the default store under LOCALAPPDATA.
    Acb 0 @('plan', "$work\backup.zip") | Out-Null
    Acb 0 @('restore', "$work\backup.zip", '--yes') | Out-Null
    Holds $autoexec 'echo source'
    Acb 0 @('undo', '--yes') | Out-Null
    Holds $autoexec 'echo target'
    Ok 'found Steam through HKCU SteamPath, restored, undid'

    # 2. cs2.exe running: a copy of a harmless system binary named cs2.exe.
    Copy-Item "$env:SystemRoot\System32\PING.EXE" "$work\cs2.exe"
    $p = Start-Process "$work\cs2.exe" -ArgumentList '-n', '300', '127.0.0.1' -PassThru -WindowStyle Hidden
    Acb 2 @('restore', "$work\backup.zip", '--yes') | Out-Null
    Holds $autoexec 'echo target'
    Stop-Process $p -Force
    & $acb undo --yes | Out-Null
    Ok 'cs2.exe running: its sections left, exit 2'

    # 3. A cfg folder the player may not add files to (deny ACE), then a locked target.
    $cfg = Split-Path $autoexec
    # icacls, not Set-Acl: a folder in a container layer can carry an ACL out of canonical order, which .NET refuses
    # to change and icacls rewrites in order.
    icacls $cfg /deny "$($identity.Name):(WD)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /deny exited $LASTEXITCODE" }
    Acb 2 @('restore', "$work\backup.zip", '--yes', '--only', 'cs2game') | Out-Null
    icacls $cfg /remove:d $identity.Name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /remove:d exited $LASTEXITCODE" }
    Holds $autoexec 'echo target'
    Ok 'cfg folder denied new files: section left, exit 2'

    $lock = [IO.File]::Open($autoexec, 'Open', 'Read', 'None')
    try { Acb 2 @('restore', "$work\backup.zip", '--yes', '--only', 'cs2game') | Out-Null } finally { $lock.Dispose() }
    Holds $autoexec 'echo target'
    Ok 'locked autoexec.cfg: section left, exit 2'

    # 4. Read-only attribute only: written, attribute kept.
    Set-ItemProperty $autoexec IsReadOnly $true
    Acb 0 @('restore', "$work\backup.zip", '--yes', '--only', 'cs2game') | Out-Null
    Holds $autoexec 'echo source'
    if (-not (Get-Item $autoexec).IsReadOnly) { throw 'read-only attribute lost' }
    & $acb undo --yes | Out-Null
    Ok 'read-only attribute: written and kept'
    Set-ItemProperty $autoexec IsReadOnly $false

    # 5. HKCU sections, real registry API on the container's own hive. The session-user check may defer them here
    #    (a container has no interactive session); the output says which.
    & $fixture -Root $steam -SeedSettings | Out-Null
    $out = & $acb backup -o "$work\settings.zip" --only gamebar,cs2exe,input,power 2>&1 | Out-String
    Info "backup of gamebar,cs2exe,input,power exited $LASTEXITCODE"; Write-Host $out
    Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled 1 -Type DWord
    $out = & $acb restore "$work\settings.zip" --yes 2>&1 | Out-String
    Info "restore of those exited $LASTEXITCODE; AutoGameModeEnabled now $((Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar').AutoGameModeEnabled)"
    Write-Host $out
    $out = & $acb undo --yes 2>&1 | Out-String
    Info "undo exited $LASTEXITCODE; AutoGameModeEnabled now $((Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar').AutoGameModeEnabled)"
    Write-Host $out
}

# Probes, both users: what this kind of Windows offers the machine-wide sections.
# Probes only report: powercfg writes to stderr where it has no power service, which Stop would make fatal.
try { Info ("powercfg /list: " + ((& "$env:SystemRoot\System32\powercfg.exe" /list 2>&1 | Out-String).Trim())) }
catch { Info "powercfg /list failed: $($_.Exception.Message)" }
Info "nvapi64.dll present: $(Test-Path "$env:SystemRoot\System32\nvapi64.dll")"
Info "EnableLUA: $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).EnableLUA)"
Write-Host "all $script:pass asserted scenarios passed"
