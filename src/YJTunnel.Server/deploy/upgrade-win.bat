@echo off
rem YJTunnel.Server 升级脚本（需管理员权限运行）
rem 用法：upgrade-win.bat [目标目录（默认 %ProgramFiles%\YJTunnel）]
rem 行为：net stop -> 仅覆盖程序文件（tunnelserver.json / customers.json / pending.json / logs 一律不动）-> net start
rem 说明：本脚本需在新版发布包目录内运行（包结构由 package-win.bat 产出）

setlocal
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%ProgramFiles%\YJTunnel"
set "PKG=%~dp0.."

echo 升级目标：%TARGET%
if not exist "%TARGET%\YJTunnel.Server.exe" (
  echo [错误] 目标目录未安装服务（先运行 install-win.bat）
  exit /b 1
)

net stop YJTunnel >nul 2>&1

rem 仅覆盖程序文件；配置与数据文件按文件名排除（.bak 备份同样保留）
robocopy "%PKG%" "%TARGET%" /E /XF tunnelserver.json customers.json pending.json /XD logs >nul
if errorlevel 8 (
  echo [错误] 文件覆盖失败（robocopy 代码 8 以上）
  exit /b 1
)

net start YJTunnel
if errorlevel 1 (
  echo [错误] 服务启动失败，请查看事件查看器
  exit /b 1
)

echo [完成] 升级完成：配置与端口分配表已原样保留，隧道客户端将自动重连。
pause
