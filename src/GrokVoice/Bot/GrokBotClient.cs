using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictate.Core.Transcription;
using Windows.Security.Credentials;

namespace GrokVoice.Bot;

/// <summary>A Grok Bot teammate from the user's account.</summary>
internal sealed record GrokBot(string Id, string Name, bool IsRunning);

/// <summary>
/// Talks to Grok Bot the way its desktop app does (the undocumented protocol the Grok Bot Raycast
/// extension uses, checked against Grok Bot 1.2.3 on Windows): a Cursor browser sign-in gives
/// tokens; api2.cursor.sh hands out the bot gateway; the gateway lists bots and takes prompts.
/// Tokens are kept in Windows Credential Manager.
/// </summary>
internal sealed class GrokBotClient
{
    private const string Backend = "https://api2.cursor.sh";
    private const string OAuthClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";
    private const string VaultResource = "Grok Voice (Grok Bot)";
    // Used when the installed Grok Bot's version can't be read; the server refuses versions it no longer supports.
    private const string FallbackVersion = "1.2.3";

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly PasswordVault _vault = new();
    private readonly SemaphoreSlim _connect = new(1, 1);
    private Gateway? _gateway;

    private sealed record Gateway(string Url, string Token, string PortToken);

    public bool IsSignedIn => Read("access") is not null;

    /// <summary>Opens the Cursor sign-in in the browser and waits (up to 10 minutes) for it to finish.</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var uuid = Guid.NewGuid().ToString();
        var url = "https://cursor.com/loginDeepControl?" + string.Join("&",
            $"challenge={challenge}", $"uuid={uuid}", "mode=login", "redirectTarget=sand", "supportsSelectedTeamLogin=true");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            using var response = await Http.GetAsync($"{Backend}/auth/poll?uuid={uuid}&verifier={verifier}", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            var body = await ReadJsonAsync(response, mutation: false, ct).ConfigureAwait(false);
            Save(Required(body, "accessToken"), Required(body, "refreshToken"));
            _gateway = null;
            return;
        }
        throw new TranscriptionException("Sign-in didn't finish within 10 minutes. Try again.");
    }

    public void SignOut()
    {
        foreach (var name in new[] { "access", "refresh" })
        {
            try
            {
                _vault.Remove(_vault.Retrieve(VaultResource, name));
            }
            catch (Exception)
            {
                // Nothing stored.
            }
        }
        _gateway = null;
    }

    public async Task<IReadOnlyList<GrokBot>> ListBotsAsync(CancellationToken ct)
    {
        var value = await CommandAsync("listAgents", new JsonObject(), mutation: false, ct).ConfigureAwait(false);
        if (value is not JsonArray list) throw new TranscriptionException("Grok Bot sent an unexpected bot list.");
        return list.OfType<JsonObject>()
            .Select(b => new GrokBot(Required(b, "id"), Required(b, "name"), b["isRunning"]?.GetValue<bool>() == true))
            .ToList();
    }

    /// <summary>
    /// Sends a prompt to a bot. Bots run on Grok Bot's servers, so (as Grok Bot 1.2 does) this goes
    /// through the Cursor backend rather than the gateway. Never retried: if delivery is uncertain,
    /// the user checks Grok Bot.
    /// </summary>
    /// <returns>The message id, to find the bot's reply with <see cref="WatchRepliesAsync"/>.</returns>
    public async Task<string> SendAsync(string botId, string prompt, CancellationToken ct,
        IReadOnlyList<string>? attachmentPaths = null, IReadOnlyList<string>? attachmentNames = null)
    {
        var messageId = Guid.NewGuid().ToString();
        var body = new JsonObject
        {
            ["agentId"] = botId,
            ["messageId"] = messageId,
            ["text"] = prompt,
            ["sentAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["isFork"] = false,
        };
        if (attachmentPaths is { Count: > 0 })
        {
            body["attachmentPaths"] = new JsonArray(attachmentPaths.Select(p => (JsonNode)p).ToArray());
            body["attachmentNames"] = new JsonArray((attachmentNames ?? attachmentPaths).Select(n => (JsonNode)n).ToArray());
        }
        var result = await BackendAsync("SendGrokBotUserMessage", body, mutation: true, ct).ConfigureAwait(false);
        if (result is JsonObject o && o["refusal"] is JsonNode refusal)
            throw new TranscriptionException($"Grok Bot refused the instruction: {refusal.ToJsonString()}");
        return messageId;
    }

    /// <summary>
    /// Uploads a file for a message, the way Grok Bot's app does when it can't use a direct upload:
    /// in chunks through UploadGrokBotAgentAttachmentChunk. The last chunk's answer has the path that
    /// goes in the message's attachmentPaths.
    /// </summary>
    public async Task<string> UploadAttachmentAsync(string botId, string fileName, byte[] data, CancellationToken ct)
    {
        const int ChunkBytes = 512 * 1024;
        var uploadId = Guid.NewGuid().ToString();
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        for (var offset = 0; ; offset += ChunkBytes)
        {
            var length = Math.Min(ChunkBytes, data.Length - offset);
            var body = new JsonObject
            {
                ["agentId"] = botId,
                ["uploadId"] = uploadId,
                ["fileName"] = fileName,
                ["offset"] = offset.ToString(),
                ["totalSize"] = data.Length.ToString(),
                ["data"] = Convert.ToBase64String(data, offset, length),
                ["sha256Hex"] = sha,
            };
            var result = await BackendAsync("UploadGrokBotAgentAttachmentChunk", body, mutation: false, ct).ConfigureAwait(false);
            if (result?["path"] is JsonValue v && v.TryGetValue<string>(out var path) && path.Length > 0) return path;
            if (offset + length >= data.Length)
                throw new TranscriptionException($"Grok Bot took {fileName} but gave no path for it.");
        }
    }

    /// <summary>
    /// Reports the bot's text replies to a message as they arrive, by polling its transcript. Our
    /// message appears with its id as clientNonce; the bot's replies are "send-message" entries with
    /// the same requestId. Stops once the replies have been quiet for <paramref name="quietAfterReply"/>,
    /// or after <paramref name="giveUpAfter"/> with no reply at all.
    /// </summary>
    public async Task WatchRepliesAsync(string botId, string messageId, Action<string> onReply,
        TimeSpan quietAfterReply, TimeSpan giveUpAfter, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var lastReply = TimeSpan.Zero;
        string? requestId = null;
        var shown = new HashSet<string>();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            var body = new JsonObject { ["agentId"] = botId, ["limit"] = 12, ["sessionId"] = "" };
            var value = await BackendAsync("ListGrokBotTranscriptEntries", body, mutation: false, ct).ConfigureAwait(false);
            var entries = (value?["entries"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(Decode)
                .Where(e => e is not null)
                .Select(e => e!)
                .OrderBy(e => e["seq"]?.GetValue<long>() ?? 0)
                .ToList();

            requestId ??= entries.FirstOrDefault(e => Text(e, "clientNonce") == messageId) is { } mine ? Text(mine, "requestId") : null;
            if (requestId is not null)
            {
                foreach (var e in entries.Where(e => Text(e, "kind") == "send-message" && Text(e, "requestId") == requestId))
                {
                    var id = Text(e, "id") ?? "";
                    if (!shown.Add(id)) continue;
                    if (e["message"] is JsonObject m && Text(m, "type") == "text" && Text(m, "content") is { Length: > 0 } content)
                    {
                        lastReply = clock.Elapsed;
                        onReply(content);
                    }
                }
            }

            if (lastReply > TimeSpan.Zero ? clock.Elapsed - lastReply > quietAfterReply : clock.Elapsed > giveUpAfter) return;
        }

        // Each entry's body is the transcript entry as JSON, base64 in Connect's JSON encoding.
        static JsonObject? Decode(JsonObject entry)
        {
            try
            {
                if (entry["body"]?.GetValue<string>() is not { Length: > 0 } b64) return null;
                var json = JsonNode.Parse(Convert.FromBase64String(b64)) as JsonObject;
                if (json is not null) json["seq"] = long.TryParse(entry["seq"]?.ToString(), out var seq) ? seq : 0;
                return json;
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        static string? Text(JsonObject o, string field) =>
            o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    // A GrokBotService call on the Cursor backend (Connect protocol, JSON), renewing the sign-in once.
    private async Task<JsonNode?> BackendAsync(string method, JsonObject body, bool mutation, CancellationToken ct)
    {
        var access = Read("access") ?? throw new TranscriptionException("Sign in to Grok Bot in Murmur's settings first.");
        for (var attempt = 0; ; attempt++)
        {
            using var request = BackendRequest(method, access, body.ToJsonString());
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new TranscriptionException(mutation
                    ? "Couldn't tell whether Grok Bot got the instruction. Check Grok Bot before sending it again."
                    : "Couldn't reach Grok Bot. Check your connection.");
            }
            using (response)
            {
                // An expired sign-in is refused before anything is sent, so renewing and retrying is safe.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0
                    && !(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Contains("no longer supported", StringComparison.OrdinalIgnoreCase))
                {
                    access = await RefreshAsync(ct).ConfigureAwait(false);
                    continue;
                }
                return await ReadJsonAsync(response, mutation, ct).ConfigureAwait(false);
            }
        }
    }

    private static HttpRequestMessage BackendRequest(string method, string accessToken, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Backend}/aiserver.v1.GrokBotService/{method}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("x-cursor-client-type", "sand");
        request.Headers.Add("x-cursor-client-source", "sand-desktop");
        request.Headers.Add("x-cursor-client-version", InstalledVersion());
        request.Headers.Add("x-sand-box-namespace", "prod");
        request.Headers.Add("Connect-Protocol-Version", "1");
        return request;
    }

    private async Task<JsonNode?> CommandAsync(string command, JsonObject body, bool mutation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var gateway = await ConnectAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{gateway.Url}/api/{command}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);
            request.Headers.Add("x-anyrun-port-token", gateway.PortToken);

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new TranscriptionException(mutation
                    ? "Couldn't tell whether Grok Bot got the instruction. Check Grok Bot before sending it again."
                    : "Couldn't reach Grok Bot. Check your connection.");
            }
            using (response)
            {
                // Gateway credentials expire; get fresh ones once. Not for a send that may have landed.
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    && attempt == 0 && !mutation)
                {
                    _gateway = null;
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) _gateway = null;
                return await ReadJsonAsync(response, mutation, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<Gateway> ConnectAsync(CancellationToken ct)
    {
        if (_gateway is { } ready) return ready;
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gateway is { } raced) return raced;
            var access = Read("access") ?? throw new TranscriptionException("Sign in to Grok Bot in Murmur's settings first.");
            JsonNode? value;
            try
            {
                value = await EnsureSandBoxAsync(access, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedException)
            {
                value = await EnsureSandBoxAsync(await RefreshAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
            var url = Required(value, "gatewayUrl").TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
                throw new TranscriptionException("Grok Bot's gateway isn't a secure address.");
            _gateway = new Gateway(url, Required(value, "gatewayToken"), Required(value, "gatewayPortToken"));
            return _gateway;
        }
        finally
        {
            _connect.Release();
        }
    }

    private async Task<JsonNode?> EnsureSandBoxAsync(string accessToken, CancellationToken ct)
    {
        using var request = BackendRequest("EnsureSandBox", accessToken, "{}");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Contains("no longer supported", StringComparison.OrdinalIgnoreCase))
                throw new TranscriptionException("Grok Bot needs updating. Update it from cursor.com/sand, then try again.");
            throw new UnauthorizedException();
        }
        return await ReadJsonAsync(response, mutation: false, ct).ConfigureAwait(false);
    }

    private async Task<string> RefreshAsync(CancellationToken ct)
    {
        var refresh = Read("refresh") ?? throw new TranscriptionException("Sign in to Grok Bot again in Murmur's settings.");
        var body = new JsonObject
        {
            ["client_id"] = OAuthClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        };
        using var response = await Http.PostAsync($"{Backend}/oauth/token",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new TranscriptionException("Your Grok Bot sign-in expired. Sign in again in Murmur's settings.");
        var value = await ReadJsonAsync(response, mutation: false, ct).ConfigureAwait(false);
        var access = Required(value, "access_token");
        var newRefresh = value?["refresh_token"]?.GetValue<string>();
        Save(access, string.IsNullOrEmpty(newRefresh) ? refresh : newRefresh);
        return access;
    }

    /// <summary>The installed Grok Bot's version, which the server checks.</summary>
    private static string InstalledVersion()
    {
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Grok Bot", "Grok Bot.exe");
            if (File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (Version.TryParse((info.ProductVersion ?? info.FileVersion ?? "").Split(' ', '+', '-')[0], out var v))
                    return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }
        catch (Exception)
        {
            // Fall back below.
        }
        return FallbackVersion;
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, bool mutation, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new TranscriptionException(mutation && status >= 500
                ? "Couldn't tell whether Grok Bot got the instruction. Check Grok Bot before sending it again."
                : status is 401 or 403 ? "Grok Bot refused the request. Sign in again in Murmur's settings."
                : $"Grok Bot returned HTTP {status}.");
        }
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            throw new TranscriptionException("Grok Bot sent a response Murmur couldn't read.");
        }
    }

    private static string Required(JsonNode? node, string field) =>
        node?[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new TranscriptionException($"Grok Bot's response had no {field}. Its protocol may have changed.");

    private string? Read(string name)
    {
        try
        {
            var credential = _vault.Retrieve(VaultResource, name);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Save(string access, string refresh)
    {
        _vault.Add(new PasswordCredential(VaultResource, "access", access));
        _vault.Add(new PasswordCredential(VaultResource, "refresh", refresh));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class UnauthorizedException : Exception;
}
