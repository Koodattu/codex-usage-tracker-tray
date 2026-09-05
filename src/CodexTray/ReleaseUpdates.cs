using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTray;

internal sealed class ReleaseUpdates : IDisposable
{
    public const string ReleasesUrl = "https://github.com/Koodattu/codex-usage-tracker-tray/releases";
    public static string CurrentVersion => typeof(ReleaseUpdates).Assembly.GetName().Version!.ToString(3);
    private readonly HttpClient http;
    private DateTimeOffset nextCheck;
    private string? lastResult;
    private bool checking;
    public ReleaseUpdates(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexTray/" + CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        if (checking) return "An update check is already running.";
        if (DateTimeOffset.UtcNow < nextCheck) return lastResult ?? "Wait a minute before checking again.";
        checking = true;
        nextCheck = DateTimeOffset.UtcNow.AddMinutes(1);
        try
        {
            using var response = await http.GetAsync("https://api.github.com/repos/Koodattu/codex-usage-tracker-tray/releases/latest", cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            {
                nextCheck = DateTimeOffset.UtcNow.AddHours(1);
                return lastResult = "GitHub has limited update checks. Try again in an hour.";
            }
            if (!response.IsSuccessStatusCode) return lastResult = "Could not check GitHub. Try again later or open Releases.";
            return lastResult = Describe(await response.Content.ReadAsStringAsync(), new Version(CurrentVersion));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return lastResult = "GitHub took too long to respond. Try again later."; }
        catch (HttpRequestException) { return lastResult = "Could not reach GitHub. Check your connection."; }
        catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidOperationException)
        { return lastResult = "Could not read release information. Open Releases to check."; }
        finally { checking = false; }
    }

    internal static string Describe(string json, Version current)
    {
        var release = Json.Parse(json);
        var tag = Json.String(release, "tag_name");
        if (!release.TryGetValue("draft", out var draft) || !(draft is bool) || (bool)draft
            || !release.TryGetValue("prerelease", out var prerelease) || !(prerelease is bool) || (bool)prerelease
            || tag == null || !System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")
            || !Version.TryParse(tag.Substring(1), out var latest)) throw new FormatException();
        return latest > current ? $"Version {latest.ToString(3)} is available. Open Releases to download it."
            : latest == current ? "You have the latest release."
            : "This build is newer than the latest published release.";
    }

    public void Dispose() => http.Dispose();
}
