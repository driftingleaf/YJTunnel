# YJTunnel 客户端（YJTunnel.Client）

开箱即用的桌面配置客户端：托盘常驻、可视化编辑端口映射、一键启停、开机自启。
基于 **Avalonia 12 + TKCode.Arc 1.0**，**NativeAOT** 发布为单文件原生可执行程序，无需安装 .NET 运行时。

## 功能

- **五态状态横幅**：未启动 / 连接中 / 在线 / 断线重连 / 等待审核 / 已封禁，颜色区分
- **端口映射编辑**：启用开关、备注、本地 IP:端口、公网端口；"说明"列自动生成访问语义
- **连接配置弹窗**：服务端地址/端口、接入令牌、公司名称（上报管理台按公司显示）
- **托盘常驻**：四色状态图标（灰/蓝/绿/橙/红）、右键菜单（显示主窗口 / 启停 / 复制公网地址 / 复制诊断 / 退出）、双击弹出主窗口
- **关闭即最小化**：点 X 回托盘，隧道不断；托盘菜单"退出"才真正退出
- **开机自启**：写 HKCU Run 键，`--minimized` 参数自启直接进托盘
- **单实例**：重复启动自动唤起已有窗口
- **诊断复制**：版本/状态/延迟/映射回执一键复制，方便发给管理员排障
- **配置自愈**：`%APPDATA%\YJTunnel\client.json` 损坏自动备份 `.bad` 并回退默认；首次启动自动生成

## 发布

```bash
# Windows（需 VS2022「使用 C++ 的桌面开发」工作负载：MSVC + Windows 11 SDK）
./scripts/publish-client.sh win-x64

# Linux（需 clang、zlib1g-dev、libkrb5-dev）
./scripts/publish-client.sh linux-x64
```

产物输出到 `dist/client/<rid>/`，单个原生可执行文件 + PDB（调试符号，可删）。

> **注意**：客户端工程不能开 `InvariantGlobalization` —— TKCode.Arc 的 `NumberField`
> 静态构造硬编码 `en-us`，全球化不变模式下抛 `CultureNotFoundException`（实测）。
> 桌面端 ICU 由操作系统提供，无部署负担。

## 使用

1. 首次启动 → 点「编辑配置」→ 填服务端地址/端口、接入令牌（管理员提供）、公司名称
2. 在「端口映射」填本地服务端口与公网端口（公网端口由管理员统一规划）
3. 点「启动」→ 状态横幅变绿即在线；托盘图标变绿
4. 外网访问 `http://服务器地址:公网端口` 即穿透到内网 `本地IP:本地端口`

## 日志

`%APPDATA%\YJTunnel\logs\client-yyyyMMdd.log`，自动保留 7 天。

## 系统要求

| 平台 | 要求 |
|---|---|
| Windows | Windows 10 22H2 / Windows 11（Avalonia 12 官方支持矩阵；Win7/8.1 未列入支持，可自行验证） |
| Linux | X11 桌面环境（Wayland 需 Avalonia 环境变量切换） |
| 运行时 | 无（NativeAOT 自包含） |
