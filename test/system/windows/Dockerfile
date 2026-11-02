# escape=`
# A Windows "player PC" for acb: Server Core with a standard user "player". Build context: a folder holding acb.exe
# (published win-x64), New-FakeSteam.ps1 and scenarios.ps1; CI assembles it (.github/workflows/ci.yml, job
# "windows-pc").
#   docker build -t acb-windows-pc -f test/system/windows/Dockerfile <context>
#   docker run --rm --isolation=process --user player acb-windows-pc     # the player's run
#   docker run --rm --isolation=process acb-windows-pc                   # ContainerAdministrator's run
# Process isolation needs the image's Windows build to match the host's: ltsc2025 (26100) on windows-2025 runners.
ARG BASE=mcr.microsoft.com/windows/servercore:ltsc2025
FROM ${BASE}
SHELL ["powershell", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop';"]

# Elevation is offered only for an acb in a folder only administrators can change: Program Files is one.
COPY acb.exe "C:/Program Files/acb/acb.exe"
COPY New-FakeSteam.ps1 scenarios.ps1 C:/acb-fixture/

# A standard (non-administrator) local account. The password exists only in this throwaway image.
RUN net user player 'Acb-Fixture-1!' /add /passwordchg:no /expires:never | Out-Null; `
    New-Item -ItemType Directory C:\Steam | Out-Null; `
    icacls C:\Steam /grant 'player:(OI)(CI)M' | Out-Null

# The scenarios write the registry, ACLs and C:\Steam; all of it is thrown away with the container.
ENV ACB_DISPOSABLE_MACHINE=1

# No Valve files here: the fixture writes Steam's layout and HKCU values at run time, so each run starts clean.
ENTRYPOINT ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\acb-fixture\\scenarios.ps1"]
