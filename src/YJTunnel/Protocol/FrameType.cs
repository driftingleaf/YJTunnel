namespace YJTunnel.Protocol;

/// <summary>
/// 隧道协议固定常量（协议规格 v1.1，冻结）。
/// </summary>
public static class WireConstants
{
    /// <summary>控制连接首字节魔数：其后为帧流。</summary>
    public const byte MagicControl = 0xC1;

    /// <summary>数据附加连接首字节魔数：其后为 attachId(8B) + sessionSecret(32B)，再后为无帧纯字节流。</summary>
    public const byte MagicData = 0xC2;

    /// <summary>单帧最大长度（Len 字段上限，含 Type 字节）：64KB。</summary>
    public const int MaxFrameSize = 64 * 1024;

    /// <summary>当前协议版本；登录时按主版本号比对，不匹配返回 LoginDenied。</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>会话密钥字节数（attach 配对鉴权用）。</summary>
    public const int SessionSecretLength = 32;

    /// <summary>attachId 字节数。</summary>
    public const int AttachIdLength = 8;
}

/// <summary>
/// 控制帧类型（帧格式：Len(4B 大端) | Type(1B) | Payload）。
/// </summary>
public enum FrameType : byte
{
    /// <summary>登录请求 C→S，Payload 为 LoginMessage JSON。</summary>
    Login = 0x01,

    /// <summary>登录应答 S→C，Payload 为 LoginRespMessage JSON。</summary>
    LoginResp = 0x02,

    /// <summary>开通道 S→C，Payload = attachId(8B 大端) + mappingIdx(4B 大端)。</summary>
    Open = 0x10,

    /// <summary>心跳 C→S，空 Payload。</summary>
    Ping = 0x20,

    /// <summary>心跳应答 S→C，空 Payload。</summary>
    Pong = 0x21,

    /// <summary>会话级错误 S→C，Payload 为 ErrorMessage JSON，随后服务端断开。</summary>
    Error = 0x30,
}

/// <summary>
/// 错误码（协议规格 v1.1）。不可重试码 = 1/2/3（客户端进终态或等待审核，不进退避重连）。
/// </summary>
public static class ErrorCodes
{
    /// <summary>成功。</summary>
    public const int Ok = 0;

    /// <summary>登录被拒（Token 无效/版本不符等），客户端终态 Failed。</summary>
    public const int LoginDenied = 1;

    /// <summary>客户被禁用，客户端终态 Failed。</summary>
    public const int ClientDisabled = 2;

    /// <summary>待审核（AutoRegister=false 且未知 clientId），客户端 5 分钟间隔重试。</summary>
    public const int PendingApproval = 3;

    /// <summary>配置已变更（管理台保存后服务端主动下发并断开），客户端立即重连取新配置。</summary>
    public const int ConfigChanged = 4;

    /// <summary>端口被占（映射级运行时状态，不导致登录失败）。</summary>
    public const int PortOccupied = 5;

    /// <summary>端口被禁（保留）。</summary>
    public const int PortForbidden = 6;

    /// <summary>协议错误。</summary>
    public const int ProtocolError = 7;

    /// <summary>服务器内部错误。</summary>
    public const int ServerError = 8;

    /// <summary>客户端处于封禁期（临时封禁未到期），客户端按封禁间隔重试。</summary>
    public const int Banned = 9;

    /// <summary>错误码是否属于"登录不可重试"（Failed/等待审核终态语义）。封禁(Banned)按间隔重试，不在其中。</summary>
    public static bool IsLoginFatal(int code) => code is LoginDenied or ClientDisabled or PendingApproval;
}
