# YJTunnel 管理控制台（YJTunnel.Admin）

服务端的**桌面管理控制台**：不必开浏览器，原生窗口管理服务器——总览、客户与端口、待审核、日志、踢下线/封禁。

支持两种模式：

- **远程管理**：连接已运行的 YJTunnel 服务端（`/api`，HTTP），可管理机房/云上的服务器；
- **本机一体机**：随包启动服务端进程（隐藏窗口，无需安装 Windows 服务），隧道与 **Web 管理台** 同时可用；适合单机部署/试用/小客户现场。
基于 **Avalonia 12 + TKCode.Arc**，**NativeAOT** 单目录发布（exe + 4 个 native dll），无需 .NET 运行时。

> Web 管理台继续保留（`http://服务器:5103`），两者共用同一套 `AdminService` 与安全模型；
> 控制台只是新增的远程管理入口。

## 功能

- **总览**：在线客户 / 占用端口 / 待审核 / 客户总数 四项指标；在线会话表（机器名/来源 IP/版本/连接数/在线时长）；绑定失败告警
- **客户与端口**：客户列表 + 端口胶囊（`本地→公网`，点击即改端口，服务端前置校验占用/唯一性/白名单）；在线/离线/封禁状态；踢下线（三档：仅踢 / 临时封禁 N 分钟 / 永久封禁）；一键解封
- **待审核**：新客户端接入申请，通过（自动分配端口）/ 拒绝
- **日志**：最近 200 条操作与事件（登录/审核/封禁/端口/配置/断线），类型着色
- **托盘**：连接状态着色（绿=已连接/灰=未连接），右键菜单（打开控制台 / 立即刷新 / 复制服务器地址 / 打开 Web 管理台 / 退出）
- **轮询**：连接后每 5 秒自动刷新；登录失效自动回到登录界面
- **单实例**：重复启动唤起已有窗口

## 发布

```bat
rem Windows（需 VS2022「使用 C++ 的桌面开发」工作负载 + Windows 11 SDK）
scripts\publish-admin-aot.bat
```

产物：`dist/client/...` 同级的 `dist/admin/win-x64/YJTunnel.Admin.exe` + 分发包 `dist/YJTunnel.Admin-win-x64.zip`。

## 使用

1. 启动 `YJTunnel.Admin.exe`，选择模式：
   - **远程管理**：填服务器地址（域名或 IP）、管理台端口（默认 5103）、管理密码（与服务端 `tunnelserver.json` 一致）
   - **本机一体机**：填配置文件路径（默认 `%APPDATA%\YJTunnel\tunnelserver.json`，缺文件自动生成含随机 Token/密码），点「启动一体机」；启动后隧道与 Web 管理台同时就绪，总览页显示控制端口/Web 地址/客户端 Token（可复制、可打开 Web）
2. 连接成功进入总览；左侧导航切换页面
2. 连接成功进入总览；左侧导航切换页面
3. 改端口：客户行内点击端口胶囊
4. 踢人：客户行「踢下线」→ 三档选择
5. 关窗即最小化到托盘，托盘菜单「退出」才真正退出

配置保存在 `%APPDATA%\YJTunnel\admin.json`（只存服务器地址/端口，不保存密码）。

## 服务端 API

控制台通过服务端 `/api` 访问，全部复用 `AdminService` 与 Cookie 认证；受 `AllowedIps` 白名单约束。

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/login` | 登录（密码，失败锁定同 Web） |
| POST | `/api/logout` | 登出 |
| GET | `/api/session` | 会话探测 |
| GET | `/api/info` | 服务端信息（控制端口/端口段/AutoRegister） |
| GET | `/api/overview` | 总览（会话/端口/绑定失败/统计） |
| GET | `/api/customers` | 客户档案 + 在线集合 |
| GET | `/api/pending` | 待审核 |
| GET | `/api/events?limit=` | 事件日志（最新 N 条） |
| POST | `/api/customers/{id}/port` | 改端口（前置校验） |
| POST | `/api/customers/{id}/kick` | 踢下线/临时封禁/永久封禁 |
| POST | `/api/customers/{id}/status` | 启用/禁用（解封） |
| POST | `/api/pending/{id}` | 通过/拒绝 |

## 安全

- 远程模式与 Web 管理台同一安全模型：管理密码 + IP 白名单（`AllowedIps`）+ 失败锁定（5 次锁 15 分钟）
- 本机一体机模式要求配置中 `Admin.Enabled=true` 且有密码（控制台用它在本地登录管理台）；一体机的 **Web 管理台仅绑定 127.0.0.1**（不暴露局域网），隧道端口仍按 `BindAddress` 监听；服务端口绑定与已安装的 Windows 服务**互斥**（服务在跑时启动会提示端口占用，先 `sc stop YJTunnel`）
- 未认证 API 返回 401 JSON（不重定向），401 时控制台自动回到登录界面
- 明文 HTTP 与 Web 一致：管理台端口不应直接暴露公网；需要 TLS 时在前面挂反向代理

## 系统要求

| 平台 | 要求 |
|---|---|
| Windows | Windows 10 22H2 / Windows 11（Avalonia 12 支持矩阵） |
| 运行时 | 无（NativeAOT 自包含） |
