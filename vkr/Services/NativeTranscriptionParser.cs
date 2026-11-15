using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace vkr.Services;

internal static class NativeTranscriptionParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TranscriptionResult Parse(IntPtr pointer, Action<IntPtr> free, string engineName)
    {
        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException($"DLL {engineName} вернула пустой ответ.");
        }

        string json;
        try
        {
            json = Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
        }
        finally
        {
            free(pointer);
        }

        NativeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<NativeResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"DLL {engineName} вернула некорректный JSON.", ex);
        }

        if (response is null)
        {
            throw new InvalidOperationException($"DLL {engineName} вернула пустой JSON.");
        }

        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? $"Ошибка распознавания {engineName}.");
        }

        var segments = (response.Segments ?? new List<NativeSegment>())
            .Select(segment => new TranscriptionSegment(
                TimeSpan.FromMilliseconds(segment.StartMs),
                TimeSpan.FromMilliseconds(segment.EndMs),
                (segment.Text ?? string.Empty).Trim(),
                segment.Probability))
            .Where(segment => segment.Text.Length > 0)
            .ToList();

        var text = (response.Text ?? string.Join(" ", segments.Select(segment => segment.Text))).Trim();
        var averageProbability = segments.Count == 0
            ? 0f
            : segments.Average(segment => segment.Probability);

        return new TranscriptionResult(
            text,
            TimeSpan.FromMilliseconds(response.DurationMs),
            segments,
            averageProbability);
    }

    private sealed class NativeResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("segments")]
        public List<NativeSegment>? Segments { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class NativeSegment
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("probability")]
        public float Probability { get; set; }
    }
}
