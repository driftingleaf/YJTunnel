#!/usr/bin/env bash
# YJTunnel.Server Windows 自包含发布（在开发机执行，产物在仓库根 dist/tunnel-win）
set -e
# 脚本位于 YJTunnel.Server/deploy/，仓库根为上两级（v1.13：产物移出项目文件夹，避免污染项目树/被误提交）
cd "$(dirname "$0")/../.."

dotnet publish YJTunnel.Server/YJTunnel.Server.csproj \
  -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=false \
  -o ../../dist/tunnel-win

echo "完成：dist/tunnel-win（拷贝整目录到服务器后运行其中的 deploy\\install-win.bat）"
