#!/usr/bin/env bash
# YJTunnel 客户端发布脚本（Windows AOT / Linux AOT）
# 用法：./publish-client.sh [win-x64|linux-x64|linux-arm64]
# 产物输出到 仓库根/dist/client/<rid>/
# 依赖：
#   win-x64  → .NET 8 SDK + VS2022「使用 C++ 的桌面开发」工作负载（含 MSVC 与 Windows 11 SDK）
#   linux-*  → .NET 8 SDK + clang/zlib1g-dev/libkrb5-dev（NativeAOT 前置）
set -euo pipefail

RID="${1:-win-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/src/YJTunnel.Client/YJTunnel.Client.csproj"
OUT="$ROOT/dist/client/$RID"

echo "==> 发布 YJTunnel.Client ($RID, NativeAOT)"
dotnet publish "$PROJ" -c Release -r "$RID" -o "$OUT" --nologo

echo "==> 产物："
ls -lh "$OUT" | grep -iE "\.exe$|YJTunnel.Client$|^total|\.so$|\.dll$" || ls -lh "$OUT"

echo "==> 完成：$OUT"
