# Samples

| 示例 | 说明 |
|---|---|
| [ConsoleClient](ConsoleClient/) | 最小控制台客户端：读取 appsettings.json，打印连接状态与端口分配，Ctrl+C 优雅退出 |
| [WindowsServiceSample](WindowsServiceSample/) | 客户端常驻 Windows 服务：随系统自启、崩溃自动重启，生产部署推荐形态 |

## 运行

```bash
# 1. 修改示例目录里的 appsettings.json（服务器地址 / Token / 转发列表）
# 2. 运行
cd samples/ConsoleClient
dotnet run
```

WindowsServiceSample 安装为服务：

```bat
cd samples\WindowsServiceSample
dotnet publish -c Release -r win-x64 --self-contained false -o publish
sc create YJTunnelClientSample binPath= "%CD%\publish\WindowsServiceSample.exe" start= auto
net start YJTunnelClientSample
```
