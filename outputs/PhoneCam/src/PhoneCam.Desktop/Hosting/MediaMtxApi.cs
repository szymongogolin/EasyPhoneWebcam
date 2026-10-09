using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PhoneCam.Desktop.Hosting;

public sealed record SlotStatus(bool Publishing, DateTimeOffset? PublishingSince, IReadOnlyList<string> Tracks, long BytesReceived);

/// <summary>Polls the loopback-only MediaMTX control API (v3) for the state of one slot.</summary>
internal sealed class MediaMtxApi : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{Ports.MediaMtxApi}/"),
        Timeout = TimeSpan.FromSeconds(2)
    };

    /// <returns>Slot status, or null when the API is unreachable.</returns>
    public async Task<SlotStatus?> GetSlotAsync(string slot)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync($"v3/paths/get/{slot}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new SlotStatus(false, null, [], 0);
            if (!response.IsSuccessStatusCode)
                return null;

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            JsonElement root = json.RootElement;
            bool ready = root.TryGetProperty("ready", out JsonElement r) && r.ValueKind == JsonValueKind.True;
            DateTimeOffset? since = root.TryGetProperty("readyTime", out JsonElement t) && t.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(t.GetString(), out DateTimeOffset parsed) ? parsed : null;
            long bytes = root.TryGetProperty("bytesReceived", out JsonElement b) && b.TryGetInt64(out long value) ? value : 0;
            var tracks = new List<string>();
            if (root.TryGetProperty("tracks", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement track in list.EnumerateArray())
                    if (track.ValueKind == JsonValueKind.String) tracks.Add(track.GetString()!);
            return new SlotStatus(ready, since, tracks, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
