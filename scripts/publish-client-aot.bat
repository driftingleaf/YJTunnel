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

rem 剔除调试符号（发布件不需要 pdb，运行不受影响）
del /q "%OUT%\*.pdb" 2>nul

rem 打 zip 分发包（AOT 产物为 exe + 4 个 native dll：Skia/GLES/HarfBuzz/onigwrap，无法嵌入单文件）
set ZIP=%~dp0..\dist\YJTunnel.Client-win-x64.zip
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%OUT:\=\\%\*' -DestinationPath '%ZIP:\=\\%'" || exit /b 1

echo ==^> 产物：
dir /b "%OUT%"
echo ==^> 分发包：%ZIP%
for %%F in ("%ZIP%") do echo     大小 %%~zF 字节
endlocal
