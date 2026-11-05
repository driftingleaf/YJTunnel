#!/usr/bin/env bash
# YJTunnel.Server Linux 自包含发布（在开发机执行，产物在仓库根 dist/tunnel-linux）
set -e
# 脚本位于 YJTunnel.Server/deploy/，仓库根为上两级（v1.13：产物移出项目文件夹）
cd "$(dirname "$0")/../.."

dotnet publish YJTunnel.Server/YJTunnel.Server.csproj \
  -c Release -r linux-x64 \
  -p:SelfContained=true -p:PublishSingleFile=false \
  -o ../../dist/tunnel-linux

echo "完成：dist/tunnel-linux（拷贝整目录到服务器 /opt/yj-tunnel 后安装 deploy/yj-tunnel.service）"
