@echo off
setlocal

pushd "%~dp0"

set "CONFIGURATION=%~1"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"

set "RUNTIME_ID=%~2"
if "%RUNTIME_ID%"=="" set "RUNTIME_ID=win-x64"

set "OUTPUT_DIR=%~dp0artifacts\publish\ui\%RUNTIME_ID%"

set "DOTNET_CLI_HOME=%~dp0.dotnet-home"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=0"
set "DOTNET_NOLOGO=1"

echo Publishing MultiServerSqlExecutor UI as a single-file executable...
echo Configuration: %CONFIGURATION%
echo Runtime: %RUNTIME_ID%
echo Output: %OUTPUT_DIR%
echo.

dotnet publish "src\MultiServerSqlExecutor.Ui\MultiServerSqlExecutor.Ui.csproj" ^
  -c "%CONFIGURATION%" ^
  -r "%RUNTIME_ID%" ^
  --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:PublishTrimmed=false ^
  /p:DebugType=None ^
  /p:DebugSymbols=false ^
  -o "%OUTPUT_DIR%"

if errorlevel 1 goto :error

echo.
echo Publish succeeded.
echo Executable:
echo   "%OUTPUT_DIR%\MultiServerSqlExecutor.Ui.exe"

popd
exit /b 0

:error
set "EXIT_CODE=%errorlevel%"
echo.
echo Publish failed with exit code %EXIT_CODE%.
popd
exit /b %EXIT_CODE%
