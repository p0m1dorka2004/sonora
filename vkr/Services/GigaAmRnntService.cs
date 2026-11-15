using System.IO;
using System.Runtime.InteropServices;

namespace vkr.Services;

public sealed class GigaAmRnntService : IDisposable
{
    private const string DllName = "sonora_gigaam";
    public const string EncoderFileName = "v3_e2e_rnnt_encoder.int8.onnx";
    public const string DecoderFileName = "v3_e2e_rnnt_decoder.int8.onnx";
    public const string JointFileName = "v3_e2e_rnnt_joint.int8.onnx";
    public const string PreprocessorFileName = "gigaam_v3.onnx";
    public const string VocabFileName = "v3_e2e_rnnt_vocab.txt";

    public GigaAmRnntService()
    {
        var directory = ResolveModelDirectory();
        EncoderPath = Path.Combine(directory, EncoderFileName);
        DecoderPath = Path.Combine(directory, DecoderFileName);
        JointPath = Path.Combine(directory, JointFileName);
        PreprocessorPath = Path.Combine(directory, PreprocessorFileName);
        VocabPath = Path.Combine(directory, VocabFileName);
    }

    public string EncoderPath { get; }
    public string DecoderPath { get; }
    public string JointPath { get; }
    public string PreprocessorPath { get; }
    public string VocabPath { get; }

    public bool IsAvailable =>
        File.Exists(EncoderPath) &&
        File.Exists(DecoderPath) &&
        File.Exists(JointPath) &&
        File.Exists(PreprocessorPath) &&
        File.Exists(VocabPath) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sonora_gigaam.dll"));

    public static string ResolveModelDirectory()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SONORA", "models", "gigaam-rnnt");
        var bundled = Path.Combine(AppContext.BaseDirectory, "models", "gigaam-rnnt");

        if (Directory.Exists(bundled)) return bundled;
        if (Directory.Exists(local)) return local;
        return bundled;
    }

    public TranscriptionResult Transcribe(string audioPath)
    {
        if (!File.Exists(EncoderPath) || !File.Exists(DecoderPath) || !File.Exists(JointPath))
        {
            throw new FileNotFoundException(
                $"Локальная модель GigaAM RNN-T не найдена в {Path.GetDirectoryName(EncoderPath)}.",
                EncoderPath);
        }

        var pointer = sonora_gigaam_rnnt_transcribe(
            EncoderPath,
            DecoderPath,
            JointPath,
            PreprocessorPath,
            VocabPath,
            audioPath);
        return NativeTranscriptionParser.Parse(pointer, sonora_gigaam_free_string, "GigaAM RNN-T");
    }

    public void Dispose() => sonora_gigaam_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sonora_gigaam_rnnt_transcribe(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string encoderPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string decoderPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string jointPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string preprocessorPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string vocabPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string audioPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sonora_gigaam_free_string(IntPtr value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void sonora_gigaam_shutdown();
}
