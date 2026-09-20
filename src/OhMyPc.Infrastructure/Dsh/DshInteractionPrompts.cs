namespace OhMyPc.Infrastructure.Dsh;

/// <summary>SSH 首次连接需要人工介入时的信息。</summary>
public sealed record DshHostKeyInfo(string Host, int Port, string Fingerprint, string KeyType);

/// <summary>
/// SSH 连接过程中需要 UI 参与的回调（Infrastructure 不依赖界面）。
/// 未提供回调时对应场景直接报错，不静默放行。
/// </summary>
public sealed class DshInteractionPrompts
{
    /// <summary>私钥已加密时请求口令；参数为密钥路径，返回 null 表示放弃。</summary>
    public Func<string, Task<string?>>? RequestKeyPassphrase { get; init; }

    /// <summary>known_hosts 无记录时请求信任服务器指纹；返回 false 拒绝连接。</summary>
    public Func<DshHostKeyInfo, Task<bool>>? RequestHostKeyTrust { get; init; }
}
