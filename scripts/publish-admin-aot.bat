@echo off
rem YJTunnel 管理控制台 AOT 发布（Windows）
rem 用法：publish-admin-aot.bat [输出目录]
rem 前置：VS2022「使用 C++ 的桌面开发」工作负载 + Windows 11 SDK
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0..\dist\admin\win-x64

rem 加载 MSVC 环境（部分环境 vswhere -latest 会漏报实例）
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -all -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if "%VSPATH%"=="" (
    echo [错误] 未找到 VS2022 C++ 工具链（vswhere -requires VC.Tools.x86.x64 无结果）
    exit /b 1
)
echo ==^> VS: %VSPATH%
call "%VSPATH%\VC\Auxiliary\Build\vcvarsall.bat" amd64 || exit /b 1

echo ==^> dotnet publish (NativeAOT win-x64) -^> %OUT%
dotnet publish "%~dp0..\src\YJTunnel.Admin\YJTunnel.Admin.csproj" -c Release -r win-x64 -o "%OUT%" --nologo -p:IlcUseEnvironmentalTools=true || exit /b 1

rem 一体机模式：附带自包含单文件服务端（同时提供隧道与 Web 管理台）
echo ==^> publish YJTunnel.Server (self-contained single-file) -^> %OUT%\server
if not exist "%OUT%\server" mkdir "%OUT%\server"
dotnet publish "%~dp0..\src\YJTunnel.Server\YJTunnel.Server.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%OUT%\server" --nologo || exit /b 1
del /q "%OUT%\server\*.pdb" 2>nul
del /q "%OUT%\server\*.staticwebassets.endpoints.json" 2>nul

rem 剔除调试符号
del /q "%OUT%\*.pdb" 2>nul

rem 打 zip 分发包（控制台 exe + native dll + server 子目录）
set ZIP=%~dp0..\dist\YJTunnel.Admin-win-x64.zip
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%OUT:\=\\%\*' -DestinationPath '%ZIP:\=\\%'" || exit /b 1

echo ==^> 产物：
dir /b "%OUT%"
echo ==^> 分发包：%ZIP%
endlocal
