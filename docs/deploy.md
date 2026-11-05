# 部署手册

## 服务端（公网机器）

### 发布

开发机执行（产物在 `src/YJTunnel.Server/publish/` 或仓库根 `dist/`）：

```bash
# Windows
bash src/YJTunnel.Server/deploy/publish-win.sh

# Linux
bash src/YJTunnel.Server/deploy/publish-linux.sh
```

或使用打包脚本（publish + zip）：

```bat
src\YJTunnel.Server\deploy\package-win.bat
```

均为 self-contained 发布，目标机无需安装 .NET 运行时。

### Windows 安装

1. 上传发布目录到服务器，以管理员身份运行：

```bat
deploy\install-win.bat "D:\Apps\YJTunnel"
```

脚本自动完成：复制文件 → 首次生成 `tunnelserver.json`（Token/管理台密码自动内置）→ 防火墙规则（5003 + 远端端口段）→ 注册服务（自启动 + 崩溃自动重启）→ 启动服务。安装过程会询问管理台来源白名单 IP（回车 = 放行所有来源）。

2. **云厂商安全组放行 TCP 5003 与远端端口段**（如 2001-4999）。管理台端口 5103 不放公网。

### Linux 安装

```bash
sudo mkdir -p /opt/yj-tunnel && sudo cp -r dist/tunnel-linux/* /opt/yj-tunnel/
sudo useradd -r -s /usr/sbin/nologin yjtunnel && sudo chown -R yjtunnel /opt/yj-tunnel
# systemd 单元：deploy/yj-tunnel.service（ExecStart=/opt/yj-tunnel/YJTunnel.Server, User=yjtunnel, Restart=always）
sudo systemctl daemon-reload && sudo systemctl enable --now yj-tunnel
sudo firewall-cmd --permanent --add-port=5003/tcp --add-port=2001-4999/tcp && sudo firewall-cmd --reload
```

### 升级与卸载

```bat
:: Windows（在新的发布包目录运行；配置与端口分配数据原样保留）
deploy\upgrade-win.bat "D:\Apps\YJTunnel"
deploy\uninstall-win.bat "D:\Apps\YJTunnel"          :: 卸载服务，保留数据
deploy\uninstall-win.bat "D:\Apps\YJTunnel" /purge   :: 连数据一起删除（不可恢复）
```

### 客户端管理台

| 配置 | 说明 |
|---|---|
| `ControlPort` | 隧道控制+数据复用端口（默认 5003） |
| `Tokens` | 允许的登录令牌列表；客户端 Token 必须在列表内 |
| `AllowPorts` / `DenyPorts` | 远端端口白名单段 / 黑名单（黑名单优先，用于保护服务器自身的其他服务端口） |
| `AutoRegister` | true=未知客户端自动建档；false=进入待审核队列，由管理员审批 |
| `Admin` | 管理台开关/端口/账号密码/失败锁定/来源白名单（空=仅本机回环） |
| `MaxPendingAttaches` / `MaxPendingPerSession` | 待配对数据通道上限（内存保护） |

启动时若发现配置含 `__GENERATE_TOKEN__` / `__GENERATE_PASSWORD__` 占位符，会自动生成强随机值写回并打印到控制台。

## 客户端（内网机器）

客户端以内嵌方式集成在宿主应用中（如 Windows 服务/桌面程序），随宿主生命周期启停；也可在任何 .NET 8 进程中独立运行。

### 配置来源与优先级

1. 宿主本地持久化存储（LiteDB 等，UI 可改）——优先
2. 宿主 `appsettings.json` 的 `Tunnel` 段——首次默认值

### 首次接入（服务端 AutoRegister=false 时）

1. 客户端登录 → 服务端返回 `PendingApproval`，客户端 UI 显示"等待管理员审核"（5 分钟间隔重试）
2. 管理台"待审核"页查看机器名/申请映射 → 通过（自动建档+分配端口）→ 客户端稍后自动上线

### 行为说明

- **断线重连**：指数退避 1→2→4→8→16→30s（±20% 抖动）；连接存活 >60s 才重置退避；主备服务器连续失败 3 次自动切换
- **心跳**：20s 一次 Ping；60s 无 Pong 判死重连；服务端 90s 无任何帧同样判死
- **端口变更**：管理台改端口后在线客户端秒级收到 `ConfigChanged`，自动重连并绑定新端口
- **Token 错误/被禁用**：终态失败，UI 明确提示，不无限重试
