using System.Text;
using OhMyPc.Core.Domain;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OhMyPc.Infrastructure.Dsh;

public sealed record SshCommandOutcome(int ExitStatus, string Output, string Error)
{
    public bool Succeeded => ExitStatus == 0;
}

/// <summary>在指定服务器上执行 bash 命令；短命令取完整输出，长命令按行流式回传。</summary>
public sealed class SshCommandRunner(SshSessionPool pool, DshConnectCredential? credential = null)
{
    public static string WrapLoginShell(string command) => $"bash -lc '{command.Replace("'", "'\\''")}'";

    /// <summary>
    /// 远端路径加引号：单引号会阻止 ~ 展开（导致创建字面 "~" 目录），因此把 ~/ 改写成
    /// "$HOME/…" —— 双引号内 $HOME 正常展开，同时对内容里的双引号转义。
    /// </summary>
    public static string QuoteRemotePath(string path)
    {
        var body = path.StartsWith("~/", StringComparison.Ordinal)
            ? "$HOME/" + path[2..]
            : path;
        return "\"" + body.Replace("\"", "\\\"") + "\"";
    }

    public async Task<SshCommandOutcome> RunAsync(
        DshServerDefinition server, string command, CancellationToken cancellationToken = default)
    {
        var client = await pool.AcquireAsync(server, cancellationToken, credential).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var cmd = client.CreateCommand(WrapLoginShell(command));
                cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                var output = cmd.Execute();
                return new SshCommandOutcome(cmd.ExitStatus ?? -1, output ?? "", cmd.Error ?? "");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException)
        {
            pool.Drop(server.Id);
            throw;
        }
    }

    /// <summary>流式执行：stdout 逐行回调 progress，返回退出码。用于安装/更新的长输出。</summary>
    public async Task<int> StreamAsync(
        DshServerDefinition server,
        string command,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var client = await pool.AcquireAsync(server, cancellationToken, credential).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var cmd = client.CreateCommand(WrapLoginShell(command));
                cmd.CommandTimeout = TimeSpan.FromMinutes(10);
                var execute = cmd.ExecuteAsync(cancellationToken);
                var errors = new StringBuilder();
                var pumpError = PumpAsync(cmd.ExtendedOutputStream, line => errors.AppendLine(line), cancellationToken);
                using var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    progress.Report(line);
                }

                await execute.ConfigureAwait(false);
                await pumpError.ConfigureAwait(false);
                return cmd.ExitStatus ?? -1;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException)
        {
            pool.Drop(server.Id);
            throw;
        }
    }

    private static async Task PumpAsync(Stream stream, Action<string> sink, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            sink(line);
        }
    }

    /// <summary>读远端文本文件；不存在或不可读时返回 null。</summary>
    public async Task<string?> ReadFileAsync(
        DshServerDefinition server,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(server, $"cat {QuoteRemotePath(remotePath)} 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.Output : null;
    }

    /// <summary>通过 stdin 把文本内容写到远端文件（配置同步用）；远端原有内容被覆盖。</summary>
    public async Task WriteFileAsync(
        DshServerDefinition server,
        string remotePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var client = await pool.AcquireAsync(server, cancellationToken, credential).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                using var cmd = client.CreateCommand(WrapLoginShell($"cat > {QuoteRemotePath(remotePath)}"));
                cmd.CommandTimeout = TimeSpan.FromSeconds(60);
                var execute = cmd.BeginExecute();
                var input = cmd.CreateInputStream();
                var bytes = Encoding.UTF8.GetBytes(content);
                await input.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                input.Close();
                cmd.EndExecute(execute);
                if ((cmd.ExitStatus ?? -1) != 0)
                {
                    throw new InvalidOperationException($"写入远端文件失败（退出码 {cmd.ExitStatus}）：{cmd.Error}");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException)
        {
            pool.Drop(server.Id);
            throw;
        }
    }
}
