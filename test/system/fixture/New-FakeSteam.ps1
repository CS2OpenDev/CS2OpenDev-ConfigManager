<#
Lays out a Windows Steam folder shaped like an installed one (CS2 installed, one signed-in account), the same files
make-fake-steam.sh writes, and, with -Register, points HKCU\Software\Valve\Steam\SteamPath at it so
acb finds it the way it finds a real install.

-Register and -SeedSettings write the current user's registry. They refuse to run unless ACB_DISPOSABLE_MACHINE is 1,
which only the container image and a disposable CI runner set: a developer's own PC must never get them.

Usage: New-FakeSteam.ps1 -Root C:\Steam [-Account 69413322] [-Autoexec 'echo x'] [-Launch '-novid'] [-Register] [-SeedSettings]
#>
param(
    [Parameter(Mandatory)] [string] $Root,
    [string] $Account = '69413322',
    [string] $Autoexec = 'echo fixture',
    [string] $Launch = '-novid',
    [switch] $Register,
    [switch] $SeedSettings
)
$ErrorActionPreference = 'Stop'

if (($Register -or $SeedSettings) -and $env:ACB_DISPOSABLE_MACHINE -ne '1') {
    throw 'Refusing to write the registry: ACB_DISPOSABLE_MACHINE is not 1. Run this only in a throwaway container or CI runner.'
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put([string] $relative, [string] $text) {
    $path = Join-Path $Root $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), $utf8)
}

$install = 'steamapps\common\Counter-Strike Global Offensive'
$cfg = "$install\game\csgo\cfg"
$user = "userdata\$Account"
$steamId = 76561197960265728 + [long]$Account
$escaped = $Root.Replace('\', '\\')

Put 'steamapps\libraryfolders.vdf' "`"libraryfolders`"`n{`n`t`"0`"`n`t{`n`t`t`"path`"`t`t`"$escaped`"`n`t`t`"apps`"`n`t`t{`n`t`t`t`"730`"`t`t`"1`"`n`t`t}`n`t}`n}`n"
Put 'steamapps\appmanifest_730.acf' "`"AppState`"`n{`n`t`"appid`"`t`t`"730`"`n`t`"installdir`"`t`t`"Counter-Strike Global Offensive`"`n}`n"
Put "$install\game\bin\win64\cs2.exe" ''
Put "$cfg\gamemode_competitive.cfg" "mp_maxrounds 24`n"
Put "$cfg\server.cfg" ''
Put "$cfg\autoexec.cfg" "$Autoexec`n"
Put "$cfg\practice\nades.cfg" "sv_cheats 1`n"
Put "$user\730\local\cfg\cs2_machine_convars.vcfg" "`"config`"`n{`n`t`"convars`"`n`t{`n`t`t`"volume`"`t`t`"0.3`"`n`t}`n}`n"
Put "$user\730\local\cfg\cs2_user_convars_0_slot0.vcfg" "`"config`"`n{`n`t`"convars`"`n`t{`n`t`t`"sensitivity`"`t`t`"1.1`"`n`t}`n}`n"
Put "$user\730\local\cfg\cs2_user_convars_0_slot0.vcfg_lastclouded" "`"config`"`n{`n`t`"convars`"`n`t{`n`t`t`"sensitivity`"`t`t`"1.1`"`n`t}`n}`n"
Put "$user\730\local\cfg\cs2_user_keys_0_slot0.vcfg" "`"config`"`n{`n`t`"bindings`"`n`t{`n`t`t`"MOUSE4`"`t`t`"slot7`"`n`t}`n}`n"
Put "$user\730\local\cfg\cs2_video.txt" "`"video.cfg`"`n{`n`t`"setting.defaultres`"`t`t`"1920`"`n}`n"
Put "$user\config\localconfig.vdf" "`"UserLocalConfigStore`"`n{`n`t`"Software`"`n`t{`n`t`t`"Valve`"`n`t`t{`n`t`t`t`"Steam`"`n`t`t`t{`n`t`t`t`t`"apps`"`n`t`t`t`t{`n`t`t`t`t`t`"730`"`n`t`t`t`t`t{`n`t`t`t`t`t`t`"LaunchOptions`"`t`t`"$Launch`"`n`t`t`t`t`t}`n`t`t`t`t}`n`t`t`t}`n`t`t}`n`t}`n}`n"
Put 'config\loginusers.vdf' "`"users`"`n{`n`t`"$steamId`"`n`t{`n`t`t`"AccountName`"`t`t`"someone`"`n`t`t`"MostRecent`"`t`t`"1`"`n`t}`n}`n"

if ($Register) {
    # Steam stores the path with forward slashes, lower-cased; acb turns them back.
    New-Item -Force -Path 'HKCU:\Software\Valve\Steam' | Out-Null
    Set-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -Value ($Root.Replace('\', '/').ToLowerInvariant())
}

if ($SeedSettings) {
    # The values the gamebar and cs2exe sections carry, as a player who tuned them would have them.
    $exe = Join-Path $Root "$install\game\bin\win64\cs2.exe"
    foreach ($k in 'HKCU:\Software\Microsoft\GameBar', 'HKCU:\System\GameConfigStore',
                   'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR',
                   'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences',
                   'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers') {
        New-Item -Force -Path $k | Out-Null
    }
    Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' AutoGameModeEnabled 0 -Type DWord
    Set-ItemProperty 'HKCU:\Software\Microsoft\GameBar' UseNexusForGameBarEnabled 0 -Type DWord
    Set-ItemProperty 'HKCU:\System\GameConfigStore' GameDVR_Enabled 0 -Type DWord
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' AppCaptureEnabled 0 -Type DWord
    Set-ItemProperty 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' $exe 'GpuPreference=2;' -Type String
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' $exe '~ DISABLEDXMAXIMIZEDWINDOWEDMODE' -Type String
}

"fake Steam at $Root (account $Account)"
