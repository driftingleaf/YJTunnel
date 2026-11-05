# 协议规格

YJTunnel 使用**单 TCP 端口承载两类连接**，靠连接的第一个字节分流：

| 首字节 | 连接类型 | 用途 |
|---|---|---|
| `0xC1` | 控制连接 | 客户端登录、心跳、接收 Open 指令；整条会话仅一条，长连接 |
| `0xC2` | 数据附加连接 | 承载一条业务连接的双向字节流；每次来访连接新开一条 |

客户端永远只发起出站连接；服务端不主动回连。

## 控制帧格式

```
+----------+--------+------------------+
| Len (4B) | Type(1B) | Payload (Len B) |
+----------+--------+------------------+
```

- `Len`：大端 int32，payload 长度（含 Type 字节），上限 64KB，越界立即断开
- 未认证阶段收到任何非 Login 帧 / 未知帧类型 → 立即断开

### 帧类型

| Type | 名称 | 方向 | Payload |
|---|---|---|---|
| 0x01 | Login | C→S | JSON：`{ver, clientId, token, appVersion, machineName, companyName, mappings:[{key, localIp, localPort, requestedRemotePort}]}` |
| 0x02 | LoginResp | S→C | JSON：`{code, msg, sessionSecret(Base64), mappings:[{key, remotePort, status, msg}]}` |
| 0x10 | Open | S→C | `attachId(8B 随机) + mappingIdx(4B)` |
| 0x20 / 0x21 | Ping / Pong | C↔S | 空 |
| 0x30 | Error | S→C | JSON：`{code, msg}`（会话级致命，服务端随后断开） |

`key` = 本地端口字符串，是映射的稳定标识（本地端口变更会产生新映射）。

### 错误码

| 码 | 含义 | 客户端行为 |
|---|---|---|
| 0 | 成功 | — |
| 1 | LoginDenied（Token/版本） | 终态失败，不重试 |
| 2 | ClientDisabled | 终态失败，不重试 |
| 3 | PendingApproval | 等待审核，5 分钟间隔重试 |
| 4 | ConfigChanged | 立即重连取新配置 |
| 5 | PortOccupied | 该映射本轮未绑定，随退避重试 |
| 7 | ProtocolError | 断开 |
| 8 | ServerError | 断开重试 |

## 登录流程

1. C→S 建连（首字节 0xC1），10s 内发送 Login
2. 服务端校验协议版本（主版本必须一致）、Token
3. 服务端查/建客户档案（AutoRegister 或待审核），按分配规则确定各映射远端端口并持久化
4. 同 clientId 旧会话 epoch 接管（强断旧控制连接，旧监听随旧会话注销释放）
5. S→C LoginResp（携带会话密钥与各映射最终端口）
6. 客户端为每个映射等待外部来访；服务端收到来访后发 Open，客户端拨数据附加连接配对

## 数据附加连接

- 客户端建连后发送 `attachId(8B) + sessionSecret(32B)`
- 服务端校验（固定时间比较）→ 与等待中的来访 socket 配对
- 配对成功后为**纯字节流双向搬运**：任一方向读到 EOF → 对端 `Shutdown(Send)`（半关闭传播）；双向结束才关闭
- 单方向读到 0 / 异常仅终止本方向，另一方向继续排空

## 会话与端口生命周期

- 客户档案归属：`远端端口 → {监听器, clientId, sessionId, epoch}`；会话注销只清理**自己 sessionId** 名下的条目
- 同 clientId 新登录：强断旧会话（epoch 接管），其名下端口由新会话重绑（1s/2s/4s 重试）
- 异 clientId：对端存活（90s 内有心跳）→ 拒绝；对端已死 → 收割后移交
- 服务端心跳收割：每 10s 扫描，`lastAliveUtc` 超 90s 的会话强断并释放资源

## 保活与判死（双向）

| 层次 | 机制 | 参数 |
|---|---|---|
| 应用层 | Ping/Pong | 20s 发送，60s 无 Pong 判死 |
| TCP | KeepAlive（best-effort，失败静默降级） | 控制连接 2min/30s；数据连接 5min/30s |
| 读超时 | 控制连接两帧间最大间隔 | 90s |

应用层心跳是唯一权威判活；TCP 参数仅为兜底，任何设置失败均静默降级（兼容 Win7/2008R2）。

## 内存保护

- 全局待配对 attach 上限 1024、单会话上限 256（`MaxPendingAttaches` / `MaxPendingPerSession`，测试可注入覆盖）：超限直接关闭来访连接
- 待配对条目 10s 超时自动清理；帧长 ≤64KB；数据泵缓冲 64KB×2 由 TCP 背压限流
