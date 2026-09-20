namespace OhMyPc.Infrastructure.Dsh;

/// <summary>连接凭据的临时覆盖：用于“测试连接”这类尚未落库密码的场景。</summary>
public sealed record DshConnectCredential(string? Password);
