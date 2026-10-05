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
set "PS=%PS% $bad=@('charts folder: ','decoded ','streaming assets: ','interop types by name',' hooked','could not be resolved','note records measure','chart folder ','the pack list has','is not an array','has no element class','class pointer could not be read','probe: ','--- before ---','--- after ---','charts folder exists?','kept ','packToAssets    ','readings=','chart[0]','audio name ','audio read index ','is not ours','jacket asked for','jacket load claimed','read bookkeeping reached','outstanding-read sets','outstanding=','noread=','a call into the game','s rendering types raised','is missing','pack screen');"
set "PS=%PS% $good=@('InFalsusChartLoader loading...','charts: ','offsets asked=','hooks installed','GameAssembly.dll','is already the name of a song this game');"
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

rem ---------------------------------------------------------------------------
rem The two lists above are what somebody thought of. This one is not a list of
rem things to look for: it takes every UTF-16 literal of 12 characters or more
rem out of the artifact, and every one of them has to be on the list below. A
rem new diagnostic therefore cannot ship without someone deciding it should -
rem which is the case a phrase list cannot catch, and one such string lived in a
rem release for three rounds that way (an argument to a helper that returns a
rem value, and so cannot be [Conditional]).
rem
rem Both byte offsets are scanned, not one: a UTF-16 literal can start at an odd
rem offset and a single stride-2 pass misses every string stored that way.
rem
rem The high byte is multiplied rather than shifted, and that is not style: in
rem Windows PowerShell a byte shifted by a byte stays a byte, so 0x68 -shl 8 is
rem 0 - the scan then reads the file one byte at a time, calls every printable
rem byte a character of its own, and reports the PE header as a string. The
rem first version of this check did exactly that.
rem
rem What it reads is the string heap - the UTF-16 literals a release can reach.
rem Metadata names (an enum member's, say) are UTF-8 and are not diagnostics.
rem
rem When it fails, decide which kind it is. A diagnostic that should not ship is
rem a bug in the code, not a line to add here. A name that a shipping message
rem needs, or one of the framework's or the version resource's own, belongs on
rem the list. The entries below are, in order: the shipping log lines (Load and
rem Error), the names the interop lookups pass (a method resolver ships too, so
rem its arguments do), the localisation and shader names the game is asked for,
rem and the version resource the SDK writes.
rem
rem Three entries are not literals at all. 'B C!D!E!F''G''', 'C D!E!F!G''H''' and
rem 'E F!G!H!I''J''' are runs of bytes inside the metadata's table stream (#~) that happen
rem to decode as printable UTF-16: this check reads the whole file, and #~ is the one
rem region that cannot hold a string literal -- literals live in #US, and the version
rem resource is its own blob. So those entries are false positives of the scan rather
rem than strings someone wrote; they are on the list because the list is where this
rem check keeps its answers, and the runs move -- or vanish -- whenever the tables
rem change size. The other two appeared as hooks were added (the third with the two
rem pause hooks), and none is in the Debug build, whose tables are laid out differently.
rem ---------------------------------------------------------------------------
set "PS=%PS% $str=New-Object 'System.Collections.Generic.HashSet[string]';"
set "PS=%PS% foreach($o in 0,1){ $c=New-Object System.Text.StringBuilder; for($i=$o;$i -lt $b.Length-1;$i+=2){ $n=$b[$i] + ($b[$i+1] * 256);"
set "PS=%PS%   if($n -ge 32 -and $n -lt 127){ [void]$c.Append([char]$n) } else { if($c.Length -ge 12){ [void]$str.Add($c.ToString()) }; [void]$c.Clear() } }"
set "PS=%PS%   if($c.Length -ge 12){ [void]$str.Add($c.ToString()) } };"
set "PS=%PS% $allow=@(' could not be read: ',' has filter ',' hooks installed',' unresolved=',''' difficulty ',''' is already the name of a song this game ',''' was left out: ',''' was not loaded. Change the id ','<exception could not be read: ','<exception whose class could not be named>','<exception with no class>','AddressableHandleAutoReleaser','AddressableHandleAutoReleaser._MIA could not be found; custom jackets will not be substituted onto the song list','Assembly Version','FileDescription','Game.Common.dll','GameAssembly.dll','GameplayBackgrounds','InFalsusChartLoader loading...','InFalsusChartLoader.dll','InternalName','LegalCopyright','MakeGenericMethod','NativeClassPtr','NativeFieldInfoPtr_','OriginalFilename','PackSongCardMember','PackVisualMemberLarge','ProductVersion','SongData could not be found; the new songs will not show','SongData._yOA could not be found; the new songs will not show','SongSelectScene','Sprites/Default','StringFileInfo','StringTypeMapping','SupportsTextureFormat','TextureFormat','UnityEngine.','UnityEngine.CoreModule','UnityEngine.CoreModule.dll','Unlit/Texture','VS_VERSION_INFO','chart loading failed: ','detour faulted, custom charts disabled: ','ifapp.Game.Common','ifapp.Game.Scenes','in its if file.','no custom song could have its audio registered','no zlib data','offsets asked=','preview_seconds','registering the custom songs failed: ','set_mainTexture','the custom songs could not be added to the game''s song list','the game''s asset manager could not be reached; no custom audio can be registered, so no custom song can be played','the game''s asset manager would not take another file','the game''s chart decoder could not be reached; no charts will load','the game''s song list refresh raised: ','SongInfo.Size','SongChartInfo.Size','PackInfo.Size','SongChartInfo','SongSelectScene','DynamicStringMapping','PackSelectSceneAssets','packToAssets','packIdTypeMapping','songIdTitleTypeMapping','songIdArtistTypeMapping','jacketIllustratorNameTypeMapping','ArtistReadingOverride','LocalizationToTitleReadingOverride','PreviewStartSeconds','PreviewEndSeconds','DisplayChartDesigner','DisplayJacketDesigner','LevelSectionIndicator',': no method by that name in this build','the custom pack could not be added; the custom songs are in the song list but have no pack of their own','packSelectSceneAssets','m_Name','PackVisualMemberSmall','lockedPackAssets','the pack row accessor could not be located in this build; the custom pack will wear whatever this build puts in its row','SingleFileSaveDataV2','GameResultsV4','MemoryPackSerializer','MemoryPack.dll','CreateDefault','Game.Data.dll','mscorlib.dll','ifapp.Game.Data','TryGetResult','IFCL_temp.sav','custom results: ','no save object','no container','the save method is not on the container in this build','the game''s save could not be reached; custom scores will go into the game''s own save file','the save folder could not be read; custom scores will go into the game''s own save file','the custom results file exists but could not be read, so it was left alone; custom scores will go into the game''s own save file','the custom results could not be prepared; custom scores will go into the game''s own save file: ','the custom results file could not be written; this session''s custom scores are not saved: ','the game''s result table could not be found; custom scores will go into the game''s own save file','IFCL self test','no self-test (the record reader could not be found)','the table failed its self-test: ','the self-test could not be run: ','EncounterResults','UpdateEncounterResult','the game''s encounter results could not be found; a custom song''s encounter result will be written into the game''s own save',': no field by that name in this build',' could not be measured off the live arrays in this build: ','the game''s song and pack tables could not be read by name in this build; no custom song will be registered','the game''s data assets could not be found by name in this build; no custom song will be registered','SongSelectScene''s payload could not be read by name in this build; which difficulty is selected will not be known, so a song with a picture per difficulty will not show one','the read bookkeeping''s fields could not be read by name in this build; a served read will not release its block','the note record''s fields could not be read by name in this build; no chart can be checked, so none will load','the note records'' stride could not be measured in this build; no chart can be checked, so none will load','the pack''s card fields could not be read by name in this build; the cards will keep the difficulty they were first built with',': no usable field by that name in this build','StringTypeMapping.Mapping could not be found on its own class in this build; the custom pack''s texts will not be written','the pack''s fields could not be read by name in this build; the custom pack keeps the game''s own visuals and text','the array holds ',' element(s) in ','there is no array to measure','SongSelectPackAssets','PackSelectLargeNonCompleted','PackSelectNonCompleted',': no class to ask in this build','the save''s difficulty could not be read by name in this build; which difficulty is selected will not be known before the song select has been visited','AddComponent','DontDestroyOnLoad','get_isPlaying','get_isPrepared','set_audioOutputMode','set_isLooping','set_playOnAwake','set_renderMode','set_targetTexture','RenderTexture','UnityEngine.Video','UnityEngine.VideoModule.dll','GeneralSaveState','GeneralSaveStateV5','LastSelectedDifficulty','InFalsusChartLoader background video','LogicalNotePlayer','B C!D!E!F''G''','C D!E!F!G''H''','no custom song could be registered','the song table does not index the way this build expects ',' carries id ','); no custom song was ','registered, because their names cannot be checked against the game''s','the song''s unlock state could not be asked of the game; the custom songs may be listed but not openable',' custom song(s) will not ','appear in the song list','get_blackTexture','E F!G!H!I''J''','the background video could not be started with the song; the background will stay black','the background video could not be placed on the chart clock; it will run out of step with the chart','the chart''s clock could not be read by name in this build; the in-play background video will not play','set_renderQueue','the background''s render queue could not be set in this build; the game''s own art behind the track will be covered');"
set "PS=%PS% $arr=@($str); [Array]::Sort($arr); $off=0;"
set "PS=%PS% foreach($s in $arr){ if($allow -notcontains $s){ Write-Host ('  OFF-LIST  '+$s); $off++ } };"
set "PS=%PS% if($off -ne 0){ Write-Host ('  '+$off+' literal(s) in the artifact are on no list - a release should carry none of them.'); $fail=1 }"
set "PS=%PS% else { Write-Host ('  ok    all '+$str.Count+' literal(s) of 12+ characters are accounted for') };"
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
