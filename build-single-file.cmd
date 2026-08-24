@echo off
setlocal

pushd "%~dp0"

set "CONFIGURATION=%~1"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"

set "RUNTIME_ID=%~2"
if "%RUNTIME_ID%"=="" set "RUNTIME_ID=win-x64"

set "DOTNET_CLI_HOME=%~dp0.dotnet-home"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=0"
set "DOTNET_NOLOGO=1"

echo Publishing MultiServerSqlExecutor as single-file executables...
echo Configuration: %CONFIGURATION%
echo Runtime: %RUNTIME_ID%
echo.

set "UI_OUTPUT_DIR=%~dp0artifacts\publish\ui\%RUNTIME_ID%"
call :publish "src\MultiServerSqlExecutor.Ui\MultiServerSqlExecutor.Ui.csproj" "%UI_OUTPUT_DIR%" "MultiServerSqlExecutor.Ui.exe"
if errorlevel 1 goto :error

set "CLI_OUTPUT_DIR=%~dp0artifacts\publish\cli\%RUNTIME_ID%"
call :publish "src\MultiServerSqlExecutor.Cli\MultiServerSqlExecutor.Cli.csproj" "%CLI_OUTPUT_DIR%" "MultiServerSqlExecutor.Cli.exe"
if errorlevel 1 goto :error

echo.
echo Publish succeeded.
echo Executables:
echo   "%UI_OUTPUT_DIR%\MultiServerSqlExecutor.Ui.exe"
echo   "%CLI_OUTPUT_DIR%\MultiServerSqlExecutor.Cli.exe"

popd
exit /b 0

:publish
echo Publishing %~1
echo Output: %~2
dotnet publish "%~1" ^
  -c "%CONFIGURATION%" ^
  -r "%RUNTIME_ID%" ^
  --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:PublishTrimmed=false ^
  /p:DebugType=None ^
  /p:DebugSymbols=false ^
  -o "%~2"
echo.
exit /b %errorlevel%

:error
set "EXIT_CODE=%errorlevel%"
echo.
echo Publish failed with exit code %EXIT_CODE%.
popd
exit /b %EXIT_CODE%