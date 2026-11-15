using System.IO;
using System.Runtime.InteropServices;

namespace vkr.Services;

public enum RecognitionEngine
{
    WhisperCpp,
    GigaAmV3,
    GigaAmV3Rnnt
}

public sealed record TranscriptionSegment(TimeSpan Start, TimeSpan End, string Text, float Probability);

public sealed record TranscriptionResult(
    string Text,
    TimeSpan AudioDuration,
    IReadOnlyList<TranscriptionSegment> Segments,
    float AverageProbability);

public sealed class NativeWhisperService : IDisposable
{
    private const string DllName = "sonora_whisper";
    public const string ModelFileName = "ggml-small.bin";

    public NativeWhisperService(string? modelPath = null)
    {
        ModelPath = modelPath ?? ResolveModelPath();
    }

    public string ModelPath { get; }

    public bool IsModelAvailable =>
        File.Exists(ModelPath) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sonora_whisper.dll"));

    public static string ResolveModelPath()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SONORA", "models", ModelFileName);
        var bundled = Path.Combine(AppContext.BaseDirectory, "models", ModelFileName);

        if (File.Exists(bundled)) return bundled;
        if (File.Exists(local)) return local;
        return bundled;
    }

    public TranscriptionResult Transcribe(string audioPath)
    {
        if (!File.Exists(ModelPath))
        {
            throw new FileNotFoundException(
                $"Локальная модель не найдена: {ModelPath}. Поместите {ModelFileName} в папку models рядом с приложением.",
                ModelPath);
        }

        var pointer = sonora_transcribe(ModelPath, audioPath);
        return NativeTranscriptionParser.Parse(pointer, sonora_free_string, "whisper.cpp");
    }

    public void Dispose() => sonora_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sonora_transcribe(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string audioPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sonora_free_string(IntPtr value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sonora_shutdown();
}
