@echo off
rem YJTunnel.Server 打包脚本（开发机运行）：publish win-x64 自包含 -> zip
rem 产物：dist\YJTunnel.Server_<版本>_win-x64.zip（上传服务器解压后运行 deploy\install-win.bat）
rem 打 zip 使用 PowerShell Compress-Archive（无需第三方压缩工具）

setlocal
pushd "%~dp0..\..\.."
set "ROOT=%CD%"
popd

set /p VERSION=版本号（如 1.0.0）：
if "%VERSION%"=="" (
  echo [错误] 版本号必填
  exit /b 1
)

echo [1/2] 发布 win-x64 自包含...
dotnet publish "%ROOT%\src\YJTunnel.Server\YJTunnel.Server.csproj" ^
  -c Release -r win-x64 ^
  -p:SelfContained=true -p:PublishSingleFile=false ^
  -o "%ROOT%\dist\tunnel-win"
if errorlevel 1 (
  echo [错误] 发布失败
  exit /b 1
)

echo [2/2] 打 zip...
if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"
del "%ROOT%\dist\YJTunnel.Server_%VERSION%_win-x64.zip" >nul 2>&1
powershell -NoProfile -Command "Compress-Archive -Path '%ROOT%\dist\tunnel-win\*' -DestinationPath '%ROOT%\dist\YJTunnel.Server_%VERSION%_win-x64.zip' -Force"
if errorlevel 1 (
  echo [错误] 打包失败
  exit /b 1
)

echo [完成] %ROOT%\dist\YJTunnel.Server_%VERSION%_win-x64.zip
pause
