@echo off
rem YJTunnel.Server 卸载脚本（需管理员权限运行）
rem 用法：uninstall-win.bat [目录（默认 %ProgramFiles%\YJTunnel）] [/purge]
rem       默认保留配置与数据；加 /purge 连目录一起删除（危险，不可恢复）

setlocal
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%ProgramFiles%\YJTunnel"

net stop YJTunnel >nul 2>&1
sc delete YJTunnel >nul 2>&1
netsh advfirewall firewall delete rule name="YJTunnel-5003" >nul 2>&1
netsh advfirewall firewall delete rule name="YJTunnel-RemotePorts" >nul 2>&1

echo [完成] 服务与防火墙规则已移除。
if /i "%~2"=="/purge" (
  rd /s /q "%TARGET%" >nul 2>&1
  echo [完成] 目录已删除：%TARGET%
) else (
  echo [保留] 配置与数据仍在：%TARGET%
  echo         如需连配置/数据一起删除：uninstall-win.bat %TARGET% /purge
)
pause
