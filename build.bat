@echo off
setlocal
cd /d "%~dp0"

rem Configuration to build. Defaults to Release; pass an argument to override,
rem e.g. "build.bat Debug".
rem
rem Release is the build that gets installed, and it carries the function only:
rem Source\Probe is not compiled into it, and every log call is [Conditional] on
rem DEBUG, so the only text it keeps is the handful of lines that say whether the
rem mod is working. Debug carries both.
rem Build Debug when something needs diagnosing.
set CONFIG=Release
if not "%~1"=="" set CONFIG=%~1

rem Warnings are errors here as well as in CI, so that "it built locally" and "it
rem built in CI" mean the same thing. The project is at zero warnings.
rem
rem -t:Rebuild is not about speed: an up-to-date project is not recompiled, and the
rem analyzers run as part of the compile, so a plain build over unchanged sources can
rem report "0 warnings" while a clean one reports CA2014 -- which is how a
rem stackalloc-in-a-loop reached a built DLL three times in one session. A build that
rem answers "did this compile" has to actually compile it.
set WARN=-warnaserror -t:Rebuild

rem CI sets CI=true and has no console to pause on (/ is itself the signal cmd
rem needs -- an unqualified "pause" in CI either errors or hangs the job).
set PAUSE=pause
if defined CI set PAUSE=

where dotnet >nul 2>&1 || goto :nodotnet

echo ============================================================
echo  InFalsusChartLoader - %CONFIG% build
echo  %CD%
echo ============================================================
echo.

dotnet build -c %CONFIG% --nologo %WARN%
if errorlevel 1 goto :failed

if /i not "%CONFIG%"=="Release" goto :built

echo.
echo Checking that the Release build carries the function only...

rem ---------------------------------------------------------------------------
rem The check is written out here rather than kept in a separate script, so that
rem this one file both builds and verifies -- which is also what CI runs.
rem
rem It cannot be a findstr: strings in a .NET assembly are UTF-16, so an ASCII
rem search reports "absent" for text that is present and the check would pass
rem forever. It compares Encoding.Unicode bytes instead.
rem
rem The PowerShell avoids every character cmd treats specially inside a quoted
rem set, which is why it reads the way it does: no double quotes, no % (except
rem the one expansion wanted), no | & ^ ! and no > or < ( -gt / -lt instead ).
rem ---------------------------------------------------------------------------
set "PS=$ErrorActionPreference='Stop';"
set "PS=%PS% $p='bin\%CONFIG%\InFalsusChartLoader.dll';"
set "PS=%PS% if(-not (Test-Path $p)){ Write-Host ('No artifact at '+$p); exit 1 };"
set "PS=%PS% $b=[IO.File]::ReadAllBytes($p); $fail=0;"
set "PS=%PS% $bad=@('charts folder: ','decoded ','streaming assets: ','interop types by name',' hooked','could not be resolved','note records measure','chart folder ','the pack list has','is not an array','has no element class','class pointer could not be read','probe: ','--- before ---','--- after ---','charts folder exists?','kept ','packToAssets    ','readings=','chart[0]','audio name ','audio read index ','is not ours','jacket asked for','jacket load claimed','read bookkeeping reached','outstanding-read sets','outstanding=','noread=','a call into the game','s rendering types raised','is missing');"
set "PS=%PS% $good=@('InFalsusChartLoader loading...','charts: ','offsets agreed=','hooks installed','GameAssembly.dll','is already the name of a song this game');"
set "PS=%PS% foreach($s in $bad){ $n=[Text.Encoding]::Unicode.GetBytes($s); $h=0;"
set "PS=%PS%   for($i=0;$i -le $b.Length-$n.Length;$i++){ $ok=$true;"
set "PS=%PS%     for($j=0;$j -lt $n.Length;$j++){ if($b[$i+$j] -ne $n[$j]){ $ok=$false; break } };"
set "PS=%PS%     if($ok){ $h++ } };"
set "PS=%PS%   if($h -gt 0){ Write-Host ('  FAIL  '+$s+'   found '+$h); $fail=1 }"
set "PS=%PS%   else { Write-Host ('  ok    '+$s) } };"
set "PS=%PS% foreach($s in $good){ $n=[Text.Encoding]::Unicode.GetBytes($s); $h=0;"
set "PS=%PS%   for($i=0;$i -le $b.Length-$n.Length;$i++){ $ok=$true;"
set "PS=%PS%     for($j=0;$j -lt $n.Length;$j++){ if($b[$i+$j] -ne $n[$j]){ $ok=$false; break } };"
set "PS=%PS%     if($ok){ $h++ } };"
set "PS=%PS%   if($h -eq 0){ Write-Host ('  FAIL  '+$s+'   missing'); $fail=1 }"
set "PS=%PS%   else { Write-Host ('  ok    '+$s+'   '+$h) } };"
set "PS=%PS% if($fail -ne 0){ Write-Host 'The Release artifact carries a diagnostic string, or is missing one it should have.'; exit 1 };"
set "PS=%PS% Write-Host 'Passed: the Release build carries the function only.';"

powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS%"
if errorlevel 1 goto :checkfailed

:built
echo.
echo Build succeeded.
echo Output: bin\%CONFIG%\InFalsusChartLoader.dll
echo.
echo Nothing was copied into the game. Installing the DLL is a separate,
echo explicit step - put it in the game's Mods folder yourself, with the Charts
echo folder beside the mod folder it lands in.
echo.
%PAUSE%
exit /b 0

:nodotnet
echo ERROR: dotnet was not found on PATH.
echo.
echo Install the .NET SDK - the project targets net6.0.
echo.
%PAUSE%
exit /b 1

:failed
echo.
echo BUILD FAILED - see the errors above.
echo.
%PAUSE%
exit /b 1

:checkfailed
echo.
echo BUILD FAILED - the Release artifact carries a diagnostic string, or is
echo missing one it should have. See the FAIL lines above.
echo.
%PAUSE%
exit /b 1
