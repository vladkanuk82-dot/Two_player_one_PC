@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

rem ===== Папка игры: аргумент -> автопоиск через Steam -> спросить вручную =====
set "GAMEDIR="
if not "%~1"=="" set "GAMEDIR=%~1"
if not defined GAMEDIR (
  echo Ищу папку игры R.E.P.O. ...
  for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0find_game.ps1"`) do set "GAMEDIR=%%P"
)
if not defined GAMEDIR (
  echo Автоматически найти игру не удалось.
  set /p "GAMEDIR=Вставьте путь к папке игры (где лежит REPO.exe): "
)
if defined GAMEDIR set "GAMEDIR=%GAMEDIR:"=%"
echo Папка игры: %GAMEDIR%

set "MANAGED=%GAMEDIR%\REPO_Data\Managed"
set "BEPINEX=%GAMEDIR%\BepInEx\core\BepInEx.dll"
set "PLUGINS=%GAMEDIR%\BepInEx\plugins"

if not exist "%MANAGED%\UnityEngine.CoreModule.dll" (
  echo [ОШИБКА] Не найдена папка игры: %GAMEDIR%
  echo Укажите правильный путь при запуске:  build.bat "путь\к\REPO"
  pause & exit /b 1
)
if not exist "%BEPINEX%" (
  echo [ОШИБКА] Не найден BepInEx: %BEPINEX%
  echo Сначала установите BepInEx в игру.
  pause & exit /b 1
)

rem ===== Ищем компилятор C#, встроенный в Windows (.NET Framework 4, ставить ничего не нужно) =====
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ОШИБКА] Не найден csc.exe ^(.NET Framework 4^).
  pause & exit /b 1
)

rem ===== Системные библиотеки берём из игры, чтобы совпадали с Unity =====
set "SYSREFS="
if exist "%MANAGED%\mscorlib.dll" (
  set "SYSREFS=/nostdlib+ /r:"%MANAGED%\mscorlib.dll" /r:"%MANAGED%\System.dll" /r:"%MANAGED%\System.Core.dll""
  if exist "%MANAGED%\netstandard.dll" set "SYSREFS=%SYSREFS% /r:"%MANAGED%\netstandard.dll""
)

set "SRC=%~dp0SecondScreenPip"
if not exist "%SRC%\Plugin.cs" set "SRC=%~dp0."
if not exist "%SRC%\Plugin.cs" (
  echo [ОШИБКА] Не найден Plugin.cs рядом с build.bat.
  echo Положите build.bat в папку, где лежит папка SecondScreenPip, либо в саму SecondScreenPip.
  pause & exit /b 1
)

if not exist "out" mkdir "out"

echo Сборка SecondScreenPip.dll ...
"%CSC%" /nologo /noconfig /target:library /optimize+ /codepage:65001 /out:"out\SecondScreenPip.dll" %SYSREFS% ^
  /r:"%BEPINEX%" ^
  /r:"%MANAGED%\UnityEngine.dll" ^
  /r:"%MANAGED%\UnityEngine.CoreModule.dll" ^
  /r:"%MANAGED%\UnityEngine.IMGUIModule.dll" ^
  /r:"%MANAGED%\UnityEngine.ImageConversionModule.dll" ^
  /r:"%MANAGED%\UnityEngine.ScreenCaptureModule.dll" ^
  /r:"%MANAGED%\UnityEngine.InputLegacyModule.dll" ^
  /r:"%GAMEDIR%\BepInEx\core\0Harmony.dll" ^
  /r:"%MANAGED%\Facepunch.Steamworks.Win64.dll" ^
  /r:"%MANAGED%\Photon3Unity3D.dll" ^
  /r:"%MANAGED%\PhotonRealtime.dll" ^
  /r:"%MANAGED%\PhotonUnityNetworking.dll" ^
  "%SRC%\*.cs"
if errorlevel 1 (
  echo.
  echo [ОШИБКА] Сборка не удалась. Пришлите текст ошибки выше целиком.
  pause & exit /b 1
)

if not exist "%PLUGINS%" mkdir "%PLUGINS%"
copy /y "out\SecondScreenPip.dll" "%PLUGINS%\" >nul
if errorlevel 1 (
  echo [ОШИБКА] Не удалось скопировать DLL в %PLUGINS%
  echo Закройте игру и повторите.
  pause & exit /b 1
)

rem ===== Старый отдельный LocalMultiplayer больше не нужен (встроен в наш мод) - убираем, чтобы не конфликтовал =====
if exist "%PLUGINS%\com.github.zehsteam.LocalMultiplayer.dll" (
  del /q "%PLUGINS%\com.github.zehsteam.LocalMultiplayer.dll"
  echo Удалён старый LocalMultiplayer.dll из plugins ^(теперь встроен в мод^).
)

echo.
echo Готово! DLL скопирована в: %PLUGINS%
echo Повторите на другой копии игры, если она лежит в другой папке.
pause
