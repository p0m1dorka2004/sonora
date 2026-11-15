using System.IO;
using System.Runtime.InteropServices;

namespace vkr.Services;

public sealed class GigaAmService : IDisposable
{
    private const string DllName = "sonora_gigaam";
    public const string ModelFileName = "v3_e2e_ctc.int8.onnx";
    public const string PreprocessorFileName = "gigaam_v3.onnx";
    public const string VocabFileName = "v3_e2e_ctc_vocab.txt";

    public GigaAmService(string? modelPath = null, string? preprocessorPath = null, string? vocabPath = null)
    {
        var directory = ResolveModelDirectory();
        ModelPath = modelPath ?? Path.Combine(directory, ModelFileName);
        PreprocessorPath = preprocessorPath ?? Path.Combine(directory, PreprocessorFileName);
        VocabPath = vocabPath ?? Path.Combine(directory, VocabFileName);
    }

    public string ModelPath { get; }
    public string PreprocessorPath { get; }
    public string VocabPath { get; }

    public bool IsAvailable =>
        File.Exists(ModelPath) &&
        File.Exists(PreprocessorPath) &&
        File.Exists(VocabPath) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sonora_gigaam.dll"));

    public static string ResolveModelDirectory()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SONORA", "models", "gigaam");
        var bundled = Path.Combine(AppContext.BaseDirectory, "models", "gigaam");

        if (Directory.Exists(bundled)) return bundled;
        if (Directory.Exists(local)) return local;
        return bundled;
    }

    public TranscriptionResult Transcribe(string audioPath)
    {
        if (!File.Exists(ModelPath))
        {
            throw new FileNotFoundException(
                $"Локальная модель GigaAM не найдена: {ModelPath}. Поместите файлы модели в папку models\\gigaam рядом с приложением.",
                ModelPath);
        }

        if (!File.Exists(PreprocessorPath))
        {
            throw new FileNotFoundException(
                $"Не найден препроцессор GigaAM: {PreprocessorPath}.",
                PreprocessorPath);
        }

        if (!File.Exists(VocabPath))
        {
            throw new FileNotFoundException(
                $"Не найден словарь GigaAM: {VocabPath}.",
                VocabPath);
        }

        var pointer = sonora_gigaam_transcribe(ModelPath, PreprocessorPath, VocabPath, audioPath);
        return NativeTranscriptionParser.Parse(pointer, sonora_gigaam_free_string, "GigaAM");
    }

    public void Dispose() => sonora_gigaam_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sonora_gigaam_transcribe(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string preprocessorPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string vocabPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string audioPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sonora_gigaam_free_string(IntPtr value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sonora_gigaam_shutdown();
}
