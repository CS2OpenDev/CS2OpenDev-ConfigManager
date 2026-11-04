<#
Runs on a disposable Windows VM (a GitHub-hosted runner): the real registry, a real power service, a real session.
The runner is thrown away after the job, which is the only reason the machine-wide sections may be restored and
undone here; on a developer's PC they must never be. It refuses to run unless ACB_DISPOSABLE_MACHINE is 1, which only
the CI job sets.

It captures the Game Bar values and the power plan as a player set them, changes them to what "another PC" has,
restores the backup, checks the registry and the active plan, undoes, and checks that both are back. The runner runs
as an administrator with User Account Control off, so the parts written through the Steam folder are left by design
(checked by acb-smoke.sh); the hotkeys are probed and printed, not asserted, as a service session may refuse them.

Usage: scenarios.ps1 -Acb <path to acb.exe> -Fixture <path to New-FakeSteam.ps1>
#>
param(
    [Parameter(Mandatory)] [string] $Acb,
    [Parameter(Mandatory)] [string] $Fixture
)
$ErrorActionPreference = 'Stop'
if ($env:ACB_DISPOSABLE_MACHINE -ne '1') {
    throw 'Refusing to run: these scenarios change this PC''s registry and power plan. ACB_DISPOSABLE_MACHINE=1 is set only on a throwaway CI runner.'
}

$work = Join-Path $env:RUNNER_TEMP ("acb-vm-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$script:pass = 0
$gameBar = 'HKCU:\Software\Microsoft\GameBar'
$gameConfig = 'HKCU:\System\GameConfigStore'

function Run-Acb([int[]] $want, [string[]] $arguments) {
    $out = & $Acb @arguments 2>&1 | Out-String
    if ($want -notcontains $LASTEXITCODE) { Write-Host $out; throw "acb $($arguments -join ' ') exited $LASTEXITCODE; expected $($want -join ' or ')" }
    Write-Host $out
}
function Ok([string] $what) { Write-Host "ok: $what"; $script:pass++ }
function Info([string] $what) { Write-Host "info: $what" }
function ActivePlan() { ([regex]'[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}').Match((& powercfg.exe /getactivescheme | Out-String).ToLowerInvariant()).Value }
function Plans() { ([regex]'[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}').Matches((& powercfg.exe /list | Out-String).ToLowerInvariant()) | ForEach-Object Value }
function Value([string] $key, [string] $name) { (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
Info "running as $($identity.Name), OS $([Environment]::OSVersion.Version)"
Info "power plans: $((Plans) -join ', '); active $(ActivePlan)"

# The player's PC: Game Mode and recording off, and a power plan other than the one this runner has.
& $Fixture -Root "$work\Steam" -SeedSettings | Out-Null
$targetPlan = ActivePlan
$playerPlan = (Plans | Where-Object { $_ -ne $targetPlan } | Select-Object -First 1)
if (-not $playerPlan) { throw 'this runner lists only one power plan, so the power part cannot be shown to change' }
& powercfg.exe /setactive $playerPlan
Run-Acb @(0) @('backup', '-o', "$work\player.zip", '--only', 'gamebar,power', '--steam-path', "$work\Steam")
Ok 'backup of gamebar and power from the real registry and powercfg'

# The other PC: Game Mode and recording on, and the runner's own plan.
Set-ItemProperty $gameBar AutoGameModeEnabled 1 -Type DWord
Set-ItemProperty $gameConfig GameDVR_Enabled 1 -Type DWord
& powercfg.exe /setactive $targetPlan

Run-Acb @(0) @('plan', "$work\player.zip")
if ((Value $gameBar AutoGameModeEnabled) -ne 1 -or (ActivePlan) -ne $targetPlan) { throw 'the plan changed something' }
Ok 'plan changed nothing'

Run-Acb @(0) @('restore', "$work\player.zip", '--yes')
if ((Value $gameBar AutoGameModeEnabled) -ne 0) { throw "AutoGameModeEnabled is $(Value $gameBar AutoGameModeEnabled) after the restore; expected 0" }
if ((Value $gameConfig GameDVR_Enabled) -ne 0) { throw "GameDVR_Enabled is $(Value $gameConfig GameDVR_Enabled) after the restore; expected 0" }
if ((ActivePlan) -ne $playerPlan) { throw "the active plan is $(ActivePlan) after the restore; expected $playerPlan" }
Ok 'restore wrote the registry values and switched the power plan'

Run-Acb @(0) @('undo', '--yes')
if ((Value $gameBar AutoGameModeEnabled) -ne 1 -or (Value $gameConfig GameDVR_Enabled) -ne 1) { throw 'undo did not put the Game Bar values back' }
if ((ActivePlan) -ne $targetPlan) { throw "the active plan is $(ActivePlan) after the undo; expected $targetPlan" }
Ok 'undo put the registry values and the power plan back'

# The hotkeys: what SystemParametersInfo allows in a runner's session, probed.
$out = & $Acb backup -o "$work\input.zip" --only input 2>&1 | Out-String
Info "backup of input exited $LASTEXITCODE"; Write-Host $out
Info "nvapi64.dll present: $(Test-Path "$env:SystemRoot\System32\nvapi64.dll")"
Write-Host "all $script:pass asserted scenarios passed"
# The hotkey probe above only reports; its exit code is not this script's.
exit 0
