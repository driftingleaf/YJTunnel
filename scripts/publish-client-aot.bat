@echo off
rem YJTunnel 客户端 AOT 发布（Windows）
rem 用法：publish-client-aot.bat [输出目录]
rem 前置：VS2022「使用 C++ 的桌面开发」工作负载 + Windows 11 SDK
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0..\dist\client\win-x64

rem 加载 MSVC 环境（绕过 vswhere -latest 过滤问题）
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -all -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if "%VSPATH%"=="" (
    echo [错误] 未找到 VS2022 C++ 工具链（vswhere -requires VC.Tools.x86.x64 无结果）
    exit /b 1
)
echo ==^> VS: %VSPATH%
call "%VSPATH%\VC\Auxiliary\Build\vcvarsall.bat" amd64 || exit /b 1

echo ==^> dotnet publish (NativeAOT win-x64) -^> %OUT%
rem IlcUseEnvironmentalTools=true：跳过 findvcvarsall（部分环境 vswhere -latest 会漏报实例），直接用 vcvars 环境里的 link/lib
dotnet publish "%~dp0..\src\YJTunnel.Client\YJTunnel.Client.csproj" -c Release -r win-x64 -o "%OUT%" --nologo -p:IlcUseEnvironmentalTools=true || exit /b 1

echo ==^> 产物：
dir /b "%OUT%"
endlocal
