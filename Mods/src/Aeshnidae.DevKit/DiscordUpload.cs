using System.Net.Http;
using System.Net.Http.Headers;

namespace Aeshnidae.DevKit;

/// <summary>
/// Posts a message with one or more files to a Discord webhook.
///
/// Webhooks take multipart/form-data: a "payload_json" part carrying the normal
/// message body, and one "files[n]" part per attachment. Nothing else is needed - no
/// bot, no token, no gateway - which is why this is the right transport for handing a
/// developer a file: they click it in the channel and it is on their PC.
/// </summary>
internal static class DiscordUpload
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public sealed record Outcome(bool Ok, string Detail);

    public sealed record Attachment(string FileName, byte[] Content);

    public static Task<Outcome> SendFileAsync(string webhookUrl, string? username, string message,
                                              string fileName, byte[] content, CancellationToken ct = default)
        => SendFilesAsync(webhookUrl, username, message, new[] { new Attachment(fileName, content) }, ct);

    public static async Task<Outcome> SendFilesAsync(string webhookUrl, string? username, string message,
                                                     IReadOnlyList<Attachment> files, CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["content"] = message,
                // Never ping anyone from an automated post, whatever the text contains.
                ["allowed_mentions"] = new { parse = Array.Empty<string>() },
            };
            if (!string.IsNullOrWhiteSpace(username))
                payload["username"] = username;

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");

            for (var i = 0; i < files.Count; i++)
            {
                var file = new ByteArrayContent(files[i].Content);
                file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                form.Add(file, $"files[{i}]", files[i].FileName);
            }

            using var response = await Http.PostAsync(webhookUrl, form, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new Outcome(true, $"{(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new Outcome(false, $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 200)}");
        }
        catch (Exception ex)
        {
            return new Outcome(false, ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
