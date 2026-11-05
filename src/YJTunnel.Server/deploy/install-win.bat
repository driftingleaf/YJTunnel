@echo off
rem YJTunnel.Server Windows 服务安装脚本（需管理员权限运行）
rem 用法：install-win.bat [安装目录（默认 %ProgramFiles%\YJTunnel）]
rem 行为：停删旧服务(支持重复安装) -> 复制发布产物 -> 首次自动生成配置
rem       -> 交互追加管理台白名单IP -> 防火墙规则 -> 注册服务(自启动+崩溃自动重启) -> 自动启动

setlocal enabledelayedexpansion
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%ProgramFiles%\YJTunnel"
set "PKG=%~dp0.."

echo 安装目录：%TARGET%
echo 包目录：%PKG%

rem ---- 0. 防呆：安装目录不能在发布包目录内（否则 xcopy 自我递归）----
echo %TARGET% | find /i "%PKG%" >nul
if not errorlevel 1 (
  echo [错误] 安装目录不能位于发布包目录内：%TARGET%
  exit /b 1
)

rem ---- 1. 旧服务：先停再删（支持重复安装/覆盖安装，同时释放 exe 文件锁）----
net stop YJTunnel >nul 2>&1
sc delete YJTunnel >nul 2>&1
timeout /t 2 /nobreak >nul

rem ---- 2. 复制发布产物 ----
if not exist "%TARGET%" mkdir "%TARGET%"
xcopy "%PKG%\*" "%TARGET%\" /e /y /i /q >nul
if errorlevel 1 (
  echo [错误] 复制文件失败
  exit /b 1
)

rem ---- 3. 配置：首次自动从模板生成（Token/管理台密码由程序首次启动自动生成）----
if not exist "%TARGET%\tunnelserver.json" (
  copy "%TARGET%\configs\tunnelserver.json" "%TARGET%\tunnelserver.json" >nul
  echo [配置] 已从模板生成 tunnelserver.json（首次启动自动生成 Token 与管理台密码）
)

rem ---- 4. 管理台白名单：可选追加运维机 IP（回车=默认 "*" 放行所有来源）----
set "ADMIN_IPS="
set /p "ADMIN_IPS=管理台(5103)来源限制：回车=放行所有(默认)；输入 IP 列表(英文逗号分隔)=仅这些 IP 可访问："
if not "!ADMIN_IPS!"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-admin-ips.ps1" -Config "%TARGET%\tunnelserver.json" -Ips "!ADMIN_IPS!"
  if errorlevel 1 (
    echo [错误] 白名单写入失败，请手工编辑 tunnelserver.json 后重试
    exit /b 1
  )
)

rem ---- 5. 防火墙（先删后加，幂等）：5003 隧道口 + 2001-4999 远端段（5103 不对公网放行）----
netsh advfirewall firewall delete rule name="YJTunnel-5003" >nul 2>&1
netsh advfirewall firewall delete rule name="YJTunnel-RemotePorts" >nul 2>&1
netsh advfirewall firewall add rule name="YJTunnel-5003" dir=in action=allow protocol=TCP localport=5003 >nul
netsh advfirewall firewall add rule name="YJTunnel-RemotePorts" dir=in action=allow protocol=TCP localport=2001-4999 >nul

rem ---- 6. 注册服务（自启动 + 崩溃自动重启）----
sc create YJTunnel binPath= "%TARGET%\YJTunnel.Server.exe" start= auto >nul
if errorlevel 1 (
  echo [错误] 服务创建失败（是否以管理员运行？）
  exit /b 1
)
sc description YJTunnel "YJTunnel reverse tunnel server" >nul
sc failure YJTunnel reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul

rem ---- 7. 启动（首次启动自动生成 Token/管理台密码，写入 tunnelserver.json 并打印到服务日志）----
net start YJTunnel
if errorlevel 1 (
  echo [错误] 服务启动失败。最常见原因：程序目录缺 tunnelserver.json（见事件查看器）
  exit /b 1
)

echo.
echo [完成] 部署完成：
echo   - 隧道控制/数据口 5003；远端端口段 2001-4999；管理台 http://IP:5103（账号 admin）
echo   - 首次启动已自动生成 Token 与管理台密码（见 tunnelserver.json），客户端 Token 需与此一致
echo   - [务必] 云厂商安全组放行 TCP 5003 与 2001-4999 整段（5103 不放公网）
echo.
pause
