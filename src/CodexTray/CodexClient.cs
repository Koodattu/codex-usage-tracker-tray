using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTray;

internal sealed class CodexClient
{
    public async Task<UsageSnapshot> ReadAsync(string executable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --listen stdio://",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        // Drain diagnostics without retaining potential account information.
        process.ErrorDataReceived += (_, __) => { };
        try
        {
            DiagnosticLog.Current?.Write("codex.starting");
            StartWithRawInput(process);
            using var job = new ProcessJob(process);
            process.BeginErrorReadLine();
            try
            {
                await RequestAsync(process, 1, "initialize", new
                {
                    clientInfo = new { name = "codex_usage_tray", title = "Codex Tray", version = typeof(CodexClient).Assembly.GetName().Version!.ToString(3) }
                }, timeout.Token).ConfigureAwait(false);
                await SendAsync(process, new { method = "initialized", @params = new { } }).ConfigureAwait(false);
                DiagnosticLog.Current?.Write("codex.reading_account");
                var accountResponse = await RequestAsync(process, 2, "account/read", new { refreshToken = false }, timeout.Token).ConfigureAwait(false);
                var account = Json.Object(accountResponse, "account");
                if (account == null) throw new UsageException("Sign in to Codex with your ChatGPT account, then refresh.", true);
                if (Json.String(account, "type") != "chatgpt") throw new UsageException("Sign in to Codex with a ChatGPT account to see usage limits.", true);
                DiagnosticLog.Current?.Write("codex.reading_limits");
                var result = await RequestAsync(process, 3, "account/rateLimits/read", new { }, timeout.Token).ConfigureAwait(false);
                var snapshot = UsageParser.Parse(result, DateTimeOffset.UtcNow);
                var identity = Json.String(account, "email");
                if (!string.IsNullOrEmpty(identity))
                {
                    using var sha = SHA256.Create();
                    snapshot.AccountKey = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(identity + "|" + Json.String(account, "planType"))));
                }
                return snapshot;
            }
            finally
            {
                // Closing stdin lets Codex shut down normally. The job also reaps descendants.
                process.StandardInput.BaseStream.Close();
                if (!await Task.Run(() => process.WaitForExit(1000)).ConfigureAwait(false) && !process.HasExited) process.Kill();
                DiagnosticLog.Current?.Write("codex.closed");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Current?.Write("codex.timeout");
            throw new UsageException("Codex took too long to respond. Retrying automatically.");
        }
        catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is InvalidOperationException)
        {
            DiagnosticLog.Current?.Write("codex.transport_failed", ex);
            throw new UsageException("Could not read Codex usage. Check your connection and Codex sign-in.");
        }
        catch (Exception ex) when (ex is ArgumentException || ex is FormatException)
        {
            DiagnosticLog.Current?.Write("codex.response_invalid", ex);
            throw new UsageException("Codex returned an unreadable response. Try updating Codex.");
        }
    }

    internal static async Task SendAsync(Process process, object value)
    {
        // The framework's stdin writer inherits the Windows console encoding and may emit a BOM.
        var bytes = Encoding.UTF8.GetBytes(Json.Serializer().Serialize(value) + "\n");
        var input = process.StandardInput.BaseStream;
        await input.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
    }

    internal static void StartWithRawInput(Process process)
    {
        // Framework 4.8 flushes the console encoding's BOM while creating redirected stdin.
        // BOM-free UTF-16 sets only the managed console cache, even in a tray app with no console.
        // We never use that text writer: SendAsync writes UTF-8 bytes directly to its pipe.
        Console.InputEncoding = new UnicodeEncoding(false, false);
        process.Start();
    }

    internal static async Task<Dictionary<string, object?>> RequestAsync(Process process, int id, string method, object parameters, CancellationToken token)
    {
        await SendAsync(process, new { id, method, @params = parameters }).ConfigureAwait(false);
        var cancelled = Task.Delay(Timeout.Infinite, token);
        while (true)
        {
            var read = process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, cancelled).ConfigureAwait(false) != read) token.ThrowIfCancellationRequested();
            var line = await read.ConfigureAwait(false);
            if (line == null) throw new UsageException("Codex closed the connection. Try updating Codex or choosing its executable.");
            var message = Json.Parse(line);
            // A notification or server request is not the reply to our request.
            if (Json.String(message, "method") != null)
            {
                if (message.TryGetValue("id", out var requestId))
                    await SendAsync(process, new { id = requestId, error = new { code = -32601, message = "This client only reads account usage." } }).ConfigureAwait(false);
                continue;
            }
            if (Json.Number(message, "id") != id) continue;
            if (Json.Object(message, "error") is Dictionary<string, object?> error)
            {
                var detail = Json.String(error, "message") ?? "";
                if (detail.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("not logged", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new UsageException("Sign in to Codex again, then refresh.", true);
                if (detail.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0 || Json.Number(error, "code") == -32001)
                    throw new UsageException("Codex is busy. Waiting before trying again.");
                throw new UsageException("Codex could not provide usage. Check your connection or update Codex.");
            }
            return Json.Object(message, "result") ?? throw new FormatException("Missing result.");
        }
    }
}
