#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#define MA_NO_DEVICE_IO
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"
#include "onnxruntime_c_api.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#define SONORA_API __declspec(dllexport)
#else
#define SONORA_API __attribute__((visibility("default")))
#endif

namespace {

constexpr int kSampleRate = 16000;
constexpr int kSubsamplingFactor = 4;

std::mutex g_mutex;
const OrtApi* g_api = nullptr;
OrtEnv* g_env = nullptr;
OrtSessionOptions* g_options = nullptr;
OrtMemoryInfo* g_memory_info = nullptr;
OrtSession* g_preprocessor = nullptr;
OrtSession* g_model = nullptr;
HMODULE g_runtime = nullptr;
std::string g_model_path;
std::string g_preprocessor_path;
std::string g_vocab_path;
std::vector<std::string> g_vocab;
int g_blank = -1;

constexpr int kRnntPredHidden = 320;
constexpr int kRnntMaxTokensPerStep = 3;

OrtSession* g_rnnt_preprocessor = nullptr;
OrtSession* g_rnnt_encoder = nullptr;
OrtSession* g_rnnt_decoder = nullptr;
OrtSession* g_rnnt_joint = nullptr;
std::string g_rnnt_preprocessor_path;
std::string g_rnnt_encoder_path;
std::string g_rnnt_decoder_path;
std::string g_rnnt_joint_path;
std::string g_rnnt_vocab_path;
std::vector<std::string> g_rnnt_vocab;
int g_rnnt_blank = -1;

using GetApiBaseFn = const OrtApiBase* (ORT_API_CALL*)();

struct OrtValueDeleter {
    void operator()(OrtValue* value) const {
        if (value != nullptr && g_api != nullptr) {
            g_api->ReleaseValue(value);
        }
    }
};

struct OrtTensorInfoDeleter {
    void operator()(OrtTensorTypeAndShapeInfo* value) const {
        if (value != nullptr && g_api != nullptr) {
            g_api->ReleaseTensorTypeAndShapeInfo(value);
        }
    }
};

using OrtValuePtr = std::unique_ptr<OrtValue, OrtValueDeleter>;
using OrtTensorInfoPtr = std::unique_ptr<OrtTensorTypeAndShapeInfo, OrtTensorInfoDeleter>;

std::string JsonEscape(const std::string& value) {
    std::string out;
    out.reserve(value.size() + 16);

    for (unsigned char c : value) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buffer[7];
                    std::snprintf(buffer, sizeof(buffer), "\\u%04x", c);
                    out += buffer;
                } else {
                    out += static_cast<char>(c);
                }
                break;
        }
    }

    return out;
}

std::string ErrorJson(const std::string& message) {
    return "{\"ok\":false,\"error\":\"" + JsonEscape(message) + "\"}";
}

char* CopyToNative(const std::string& value) {
    auto* result = static_cast<char*>(std::malloc(value.size() + 1));
    if (result == nullptr) return nullptr;
    std::memcpy(result, value.c_str(), value.size() + 1);
    return result;
}

std::wstring ModuleDirectory() {
    HMODULE module = nullptr;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&ModuleDirectory),
        &module);

    wchar_t path[MAX_PATH] = {};
    const DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    std::wstring directory(path, length);
    const auto slash = directory.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring(L".") : directory.substr(0, slash);
}

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (length <= 0) return {};
    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), length);
    return result;
}

std::string OrtError(OrtStatus* status) {
    if (status == nullptr) return "Неизвестная ошибка ONNX Runtime.";
    std::string message = "Ошибка ONNX Runtime.";
    if (g_api != nullptr) {
        const char* text = g_api->GetErrorMessage(status);
        if (text != nullptr) message = text;
        g_api->ReleaseStatus(status);
    }
    return message;
}

std::vector<int64_t> ShapeOf(OrtValue* value) {
    OrtTensorTypeAndShapeInfo* raw = nullptr;
    if (g_api->GetTensorTypeAndShape(value, &raw) != nullptr) {
        return {};
    }

    OrtTensorInfoPtr info(raw);
    size_t count = 0;
    if (g_api->GetDimensionsCount(info.get(), &count) != nullptr) {
        return {};
    }

    std::vector<int64_t> shape(count);
    if (count > 0 && g_api->GetDimensions(info.get(), shape.data(), count) != nullptr) {
        return {};
    }
    return shape;
}

bool LoadRuntime(std::string& error) {
    if (g_api != nullptr) return true;

    const std::wstring directory = ModuleDirectory();
    LoadLibraryW((directory + L"\\onnxruntime_providers_shared.dll").c_str());
    g_runtime = LoadLibraryW((directory + L"\\onnxruntime.dll").c_str());
    if (g_runtime == nullptr) {
        error = "Не удалось загрузить onnxruntime.dll рядом с sonora_gigaam.dll.";
        return false;
    }

    auto get_api_base = reinterpret_cast<GetApiBaseFn>(GetProcAddress(g_runtime, "OrtGetApiBase"));
    if (get_api_base == nullptr) {
        error = "В onnxruntime.dll не найдена точка входа OrtGetApiBase.";
        return false;
    }

    const OrtApiBase* base = get_api_base();
    if (base == nullptr) {
        error = "ONNX Runtime не вернула API.";
        return false;
    }

    g_api = base->GetApi(ORT_API_VERSION);
    if (g_api == nullptr) {
        error = "Версия ONNX Runtime несовместима с sonora_gigaam.dll.";
        return false;
    }

    OrtStatus* status = g_api->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "sonora-gigaam", &g_env);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }

    status = g_api->CreateSessionOptions(&g_options);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }

    const int threads = std::max(2, std::min(8, static_cast<int>(std::thread::hardware_concurrency()) / 2));
    g_api->SetIntraOpNumThreads(g_options, threads);
    g_api->SetSessionGraphOptimizationLevel(g_options, ORT_ENABLE_ALL);

    status = g_api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &g_memory_info);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }

    return true;
}

void ReleaseSessions() {
    if (g_api == nullptr) return;
    if (g_model != nullptr) {
        g_api->ReleaseSession(g_model);
        g_model = nullptr;
    }
    if (g_preprocessor != nullptr) {
        g_api->ReleaseSession(g_preprocessor);
        g_preprocessor = nullptr;
    }
    g_model_path.clear();
    g_preprocessor_path.clear();
    g_vocab_path.clear();
    g_vocab.clear();
    g_blank = -1;
}

void ReleaseRnntSessions() {
    if (g_api == nullptr) return;
    if (g_rnnt_joint != nullptr) {
        g_api->ReleaseSession(g_rnnt_joint);
        g_rnnt_joint = nullptr;
    }
    if (g_rnnt_decoder != nullptr) {
        g_api->ReleaseSession(g_rnnt_decoder);
        g_rnnt_decoder = nullptr;
    }
    if (g_rnnt_encoder != nullptr) {
        g_api->ReleaseSession(g_rnnt_encoder);
        g_rnnt_encoder = nullptr;
    }
    if (g_rnnt_preprocessor != nullptr) {
        g_api->ReleaseSession(g_rnnt_preprocessor);
        g_rnnt_preprocessor = nullptr;
    }
    g_rnnt_preprocessor_path.clear();
    g_rnnt_encoder_path.clear();
    g_rnnt_decoder_path.clear();
    g_rnnt_joint_path.clear();
    g_rnnt_vocab_path.clear();
    g_rnnt_vocab.clear();
    g_rnnt_blank = -1;
}

bool LoadVocabFile(
    const std::string& path,
    std::vector<std::string>& vocab,
    int& blank,
    std::string& error) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        error = "Не удалось открыть словарь GigaAM: " + path;
        return false;
    }

    std::vector<std::pair<int, std::string>> entries;
    std::string line;
    int max_id = -1;

    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;

        const auto separator = line.rfind(' ');
        if (separator == std::string::npos) continue;

        const std::string token = line.substr(0, separator);
        const int id = std::atoi(line.c_str() + separator + 1);
        if (id < 0) continue;

        entries.emplace_back(id, token);
        max_id = std::max(max_id, id);
    }

    if (max_id < 0) {
        error = "Словарь GigaAM пуст.";
        return false;
    }

    vocab.assign(static_cast<size_t>(max_id) + 1, std::string());
    for (const auto& entry : entries) {
        vocab[static_cast<size_t>(entry.first)] = entry.second;
    }

    blank = -1;
    for (size_t i = 0; i < vocab.size(); ++i) {
        if (vocab[i] == "<blk>") {
            blank = static_cast<int>(i);
            break;
        }
    }

    if (blank < 0) {
        error = "В словаре GigaAM не найден токен <blk>.";
        return false;
    }

    return true;
}

bool LoadVocab(const std::string& path, std::string& error) {
    return LoadVocabFile(path, g_vocab, g_blank, error);
}

bool CreateSession(const std::string& path, OrtSession** session, std::string& error) {
    const std::wstring wide = Utf8ToWide(path);
    OrtStatus* status = g_api->CreateSession(g_env, wide.c_str(), g_options, session);
    if (status != nullptr) {
        error = "Не удалось загрузить модель GigaAM: " + path + "\n" + OrtError(status);
        return false;
    }
    return true;
}

bool EnsureSessions(
    const std::string& model_path,
    const std::string& preprocessor_path,
    const std::string& vocab_path,
    std::string& error) {
    if (g_model != nullptr && g_preprocessor != nullptr &&
        g_model_path == model_path &&
        g_preprocessor_path == preprocessor_path &&
        g_vocab_path == vocab_path) {
        return true;
    }

    ReleaseSessions();

    if (!CreateSession(preprocessor_path, &g_preprocessor, error)) {
        ReleaseSessions();
        return false;
    }

    if (!CreateSession(model_path, &g_model, error)) {
        ReleaseSessions();
        return false;
    }

    if (!LoadVocab(vocab_path, error)) {
        ReleaseSessions();
        return false;
    }

    g_model_path = model_path;
    g_preprocessor_path = preprocessor_path;
    g_vocab_path = vocab_path;
    return true;
}

bool EnsureRnntSessions(
    const std::string& encoder_path,
    const std::string& decoder_path,
    const std::string& joint_path,
    const std::string& preprocessor_path,
    const std::string& vocab_path,
    std::string& error) {
    if (g_rnnt_encoder != nullptr && g_rnnt_decoder != nullptr && g_rnnt_joint != nullptr &&
        g_rnnt_preprocessor != nullptr &&
        g_rnnt_encoder_path == encoder_path &&
        g_rnnt_decoder_path == decoder_path &&
        g_rnnt_joint_path == joint_path &&
        g_rnnt_preprocessor_path == preprocessor_path &&
        g_rnnt_vocab_path == vocab_path) {
        return true;
    }

    ReleaseRnntSessions();

    if (!CreateSession(preprocessor_path, &g_rnnt_preprocessor, error)) {
        ReleaseRnntSessions();
        return false;
    }

    if (!CreateSession(encoder_path, &g_rnnt_encoder, error)) {
        ReleaseRnntSessions();
        return false;
    }

    if (!CreateSession(decoder_path, &g_rnnt_decoder, error)) {
        ReleaseRnntSessions();
        return false;
    }

    if (!CreateSession(joint_path, &g_rnnt_joint, error)) {
        ReleaseRnntSessions();
        return false;
    }

    if (!LoadVocabFile(vocab_path, g_rnnt_vocab, g_rnnt_blank, error)) {
        ReleaseRnntSessions();
        return false;
    }

    g_rnnt_encoder_path = encoder_path;
    g_rnnt_decoder_path = decoder_path;
    g_rnnt_joint_path = joint_path;
    g_rnnt_preprocessor_path = preprocessor_path;
    g_rnnt_vocab_path = vocab_path;
    return true;
}

bool DecodeAudio(const std::string& audio_path, std::vector<float>& pcm, std::string& error) {
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 1, kSampleRate);
    ma_decoder decoder;
    ma_result result = ma_decoder_init_file(audio_path.c_str(), &config, &decoder);
    if (result != MA_SUCCESS) {
        error = "Не удалось открыть аудиофайл (поддерживаются WAV/MP3/FLAC): " + audio_path;
        return false;
    }

    ma_uint64 length = 0;
    ma_decoder_get_length_in_pcm_frames(&decoder, &length);

    if (length > 0) {
        pcm.resize(static_cast<size_t>(length));
        ma_uint64 read = 0;
        result = ma_decoder_read_pcm_frames(&decoder, pcm.data(), length, &read);
        if (result != MA_SUCCESS) {
            ma_decoder_uninit(&decoder);
            error = "Не удалось прочитать аудиофайл: " + audio_path;
            return false;
        }
        pcm.resize(static_cast<size_t>(read));
    } else {
        float chunk[4096];
        ma_uint64 read = 0;
        while (true) {
            result = ma_decoder_read_pcm_frames(&decoder, chunk, 4096, &read);
            if (result != MA_SUCCESS || read == 0) break;
            pcm.insert(pcm.end(), chunk, chunk + read);
            if (pcm.size() > static_cast<size_t>(kSampleRate) * 60ULL * 30ULL) {
                ma_decoder_uninit(&decoder);
                error = "Аудиофайл слишком длинный (максимум 30 минут).";
                return false;
            }
        }
    }

    ma_decoder_uninit(&decoder);

    if (pcm.empty()) {
        error = "Аудиофайл пуст или не содержит речи.";
        return false;
    }

    return true;
}

bool RunPreprocessorFor(
    OrtSession* session,
    const std::vector<float>& pcm,
    OrtValuePtr& features,
    OrtValuePtr& feature_lengths,
    std::string& error) {
    const int64_t waveform_shape[2] = {1, static_cast<int64_t>(pcm.size())};
    OrtValue* waveform_raw = nullptr;
    OrtStatus* status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        const_cast<float*>(pcm.data()),
        pcm.size() * sizeof(float),
        waveform_shape,
        2,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
        &waveform_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr waveform(waveform_raw);

    int64_t length_value = static_cast<int64_t>(pcm.size());
    const int64_t length_shape[1] = {1};
    OrtValue* length_raw = nullptr;
    status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        &length_value,
        sizeof(length_value),
        length_shape,
        1,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
        &length_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr length_tensor(length_raw);

    const char* input_names[] = {"waveforms", "waveforms_lens"};
    const OrtValue* inputs[] = {waveform.get(), length_tensor.get()};
    const char* output_names[] = {"features", "features_lens"};
    OrtValue* outputs[2] = {nullptr, nullptr};

    status = g_api->Run(
        session,
        nullptr,
        input_names,
        inputs,
        2,
        output_names,
        2,
        outputs);
    if (status != nullptr) {
        error = "Препроцессор GigaAM: " + OrtError(status);
        return false;
    }

    features.reset(outputs[0]);
    feature_lengths.reset(outputs[1]);
    if (!features || !feature_lengths) {
        error = "Препроцессор GigaAM вернул пустые данные.";
        return false;
    }
    return true;
}

bool RunPreprocessor(
    const std::vector<float>& pcm,
    OrtValuePtr& features,
    OrtValuePtr& feature_lengths,
    std::string& error) {
    return RunPreprocessorFor(g_preprocessor, pcm, features, feature_lengths, error);
}

bool RunModel(
    OrtValue* features,
    OrtValue* feature_lengths,
    OrtValuePtr& log_probs,
    std::string& error) {
    const char* input_names[] = {"features", "feature_lengths"};
    const OrtValue* inputs[] = {features, feature_lengths};
    const char* output_names[] = {"log_probs"};
    OrtValue* output = nullptr;

    OrtStatus* status = g_api->Run(
        g_model,
        nullptr,
        input_names,
        inputs,
        2,
        output_names,
        1,
        &output);
    if (status != nullptr) {
        error = "Модель GigaAM: " + OrtError(status);
        return false;
    }

    log_probs.reset(output);
    if (!log_probs) {
        error = "Модель GigaAM вернула пустой результат.";
        return false;
    }
    return true;
}

std::string NormalizeText(const std::string& input) {
    std::string out;
    out.reserve(input.size());
    bool pending_space = false;

    for (unsigned char c : input) {
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
            pending_space = true;
            continue;
        }

        if (pending_space && !out.empty()) {
            const bool punctuation =
                c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?' ||
                c == ')' || c == ']' || c == '}' || c == '%';
            if (!punctuation) out.push_back(' ');
        }

        pending_space = false;
        out.push_back(static_cast<char>(c));
    }

    const std::string em_dash = "\xE2\x80\x94";
    const std::string en_dash = "\xE2\x80\x93";
    for (const auto& dash : {em_dash, en_dash}) {
        const std::string spaced = " " + dash;
        size_t position = 0;
        while ((position = out.find(spaced, position)) != std::string::npos) {
            out.replace(position, spaced.size(), dash);
        }
    }

    return out;
}

std::string ReplaceWordSeparator(std::string text) {
    const std::string separator = "\xE2\x96\x81";
    size_t position = 0;
    while ((position = text.find(separator, position)) != std::string::npos) {
        text.replace(position, separator.size(), " ");
        position += 1;
    }
    return text;
}

std::string DecodeCtc(const float* logits, int64_t sequence_length, int64_t vocab_size, int64_t feature_length) {
    int64_t output_length = (feature_length - 1) / kSubsamplingFactor + 1;
    output_length = std::max<int64_t>(0, std::min(output_length, sequence_length));

    int previous = g_blank;
    double probability_sum = 0.0;
    int emitted = 0;
    std::string text;

    for (int64_t step = 0; step < output_length; ++step) {
        const float* row = logits + static_cast<size_t>(step) * static_cast<size_t>(vocab_size);
        int best = 0;
        float best_value = row[0];
        for (int64_t token = 1; token < vocab_size; ++token) {
            if (row[token] > best_value) {
                best_value = row[token];
                best = static_cast<int>(token);
            }
        }

        if (best != g_blank && best != previous && best >= 0 && static_cast<size_t>(best) < g_vocab.size()) {
            text += g_vocab[static_cast<size_t>(best)];
            probability_sum += std::exp(best_value);
            ++emitted;
        }
        previous = best;
    }

    return NormalizeText(ReplaceWordSeparator(text));
}

bool ReadInt64Value(OrtValue* value, int64_t& result) {
    OrtTensorTypeAndShapeInfo* raw = nullptr;
    if (g_api->GetTensorTypeAndShape(value, &raw) != nullptr) {
        return false;
    }

    OrtTensorInfoPtr info(raw);
    ONNXTensorElementDataType type;
    if (g_api->GetTensorElementType(info.get(), &type) != nullptr) {
        return false;
    }

    void* data = nullptr;
    if (g_api->GetTensorMutableData(value, &data) != nullptr || data == nullptr) {
        return false;
    }

    if (type == ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64) {
        result = *static_cast<int64_t*>(data);
        return true;
    }

    if (type == ONNX_TENSOR_ELEMENT_DATA_TYPE_INT32) {
        result = *static_cast<int32_t*>(data);
        return true;
    }

    return false;
}

bool RunRnntEncoder(
    OrtValue* features,
    OrtValue* feature_lengths,
    OrtValuePtr& encoded,
    OrtValuePtr& encoded_lengths,
    std::string& error) {
    const char* input_names[] = {"audio_signal", "length"};
    const OrtValue* inputs[] = {features, feature_lengths};
    const char* output_names[] = {"encoded", "encoded_len"};
    OrtValue* outputs[2] = {nullptr, nullptr};

    OrtStatus* status = g_api->Run(
        g_rnnt_encoder,
        nullptr,
        input_names,
        inputs,
        2,
        output_names,
        2,
        outputs);
    if (status != nullptr) {
        error = "Энкодер GigaAM RNN-T: " + OrtError(status);
        return false;
    }

    encoded.reset(outputs[0]);
    encoded_lengths.reset(outputs[1]);
    if (!encoded || !encoded_lengths) {
        error = "Энкодер GigaAM RNN-T вернул пустой результат.";
        return false;
    }
    return true;
}

bool RunRnntDecoder(
    int token,
    OrtValue* hidden,
    OrtValue* cell,
    OrtValuePtr& decoder_output,
    OrtValuePtr& next_hidden,
    OrtValuePtr& next_cell,
    std::string& error) {
    int64_t token_value = token;
    const int64_t token_shape[2] = {1, 1};
    OrtValue* token_raw = nullptr;
    OrtStatus* status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        &token_value,
        sizeof(token_value),
        token_shape,
        2,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64,
        &token_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr token_tensor(token_raw);

    const char* input_names[] = {"x", "h.1", "c.1"};
    const OrtValue* inputs[] = {token_tensor.get(), hidden, cell};
    const char* output_names[] = {"dec", "h", "c"};
    OrtValue* outputs[3] = {nullptr, nullptr, nullptr};

    status = g_api->Run(
        g_rnnt_decoder,
        nullptr,
        input_names,
        inputs,
        3,
        output_names,
        3,
        outputs);
    if (status != nullptr) {
        error = "Декодер GigaAM RNN-T: " + OrtError(status);
        return false;
    }

    decoder_output.reset(outputs[0]);
    next_hidden.reset(outputs[1]);
    next_cell.reset(outputs[2]);
    if (!decoder_output || !next_hidden || !next_cell) {
        error = "Декодер GigaAM RNN-T вернул пустой результат.";
        return false;
    }
    return true;
}

bool RunRnntJoiner(
    const std::vector<float>& encoder_step,
    OrtValue* decoder_output,
    OrtValuePtr& joint,
    std::string& error) {
    const std::vector<int64_t> decoder_shape = ShapeOf(decoder_output);
    if (decoder_shape.size() != 3 || decoder_shape[2] <= 0) {
        error = "Декодер GigaAM RNN-T вернул тензор неожиданной формы.";
        return false;
    }

    const int64_t hidden = decoder_shape[2];
    void* decoder_data = nullptr;
    if (g_api->GetTensorMutableData(decoder_output, &decoder_data) != nullptr || decoder_data == nullptr) {
        error = "Не удалось прочитать выход декодера GigaAM RNN-T.";
        return false;
    }

    const int64_t encoder_shape[3] = {1, static_cast<int64_t>(encoder_step.size()), 1};
    OrtValue* encoder_raw = nullptr;
    OrtStatus* status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        const_cast<float*>(encoder_step.data()),
        encoder_step.size() * sizeof(float),
        encoder_shape,
        3,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
        &encoder_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr encoder_tensor(encoder_raw);

    const int64_t decoder_view_shape[3] = {1, hidden, 1};
    OrtValue* decoder_raw = nullptr;
    status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        decoder_data,
        static_cast<size_t>(hidden) * sizeof(float),
        decoder_view_shape,
        3,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
        &decoder_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr decoder_view(decoder_raw);

    const char* input_names[] = {"enc", "dec"};
    const OrtValue* inputs[] = {encoder_tensor.get(), decoder_view.get()};
    const char* output_names[] = {"joint"};
    OrtValue* output = nullptr;

    status = g_api->Run(
        g_rnnt_joint,
        nullptr,
        input_names,
        inputs,
        2,
        output_names,
        1,
        &output);
    if (status != nullptr) {
        error = "Джойнер GigaAM RNN-T: " + OrtError(status);
        return false;
    }

    joint.reset(output);
    if (!joint) {
        error = "Джойнер GigaAM RNN-T вернул пустой результат.";
        return false;
    }
    return true;
}

bool RunRnnt(
    OrtValue* features,
    OrtValue* feature_lengths,
    std::string& text,
    std::string& error) {
    OrtValuePtr encoded;
    OrtValuePtr encoded_lengths;
    if (!RunRnntEncoder(features, feature_lengths, encoded, encoded_lengths, error)) {
        return false;
    }

    const std::vector<int64_t> encoder_shape = ShapeOf(encoded.get());
    if (encoder_shape.size() != 3 || encoder_shape[1] <= 0 || encoder_shape[2] <= 0) {
        error = "Энкодер GigaAM RNN-T вернул тензор неожиданной формы.";
        return false;
    }

    const int64_t hidden = encoder_shape[1];
    const int64_t frames = encoder_shape[2];
    int64_t encoder_length = 0;
    if (!ReadInt64Value(encoded_lengths.get(), encoder_length)) {
        error = "Не удалось прочитать длину выхода энкодера GigaAM RNN-T.";
        return false;
    }
    encoder_length = std::max<int64_t>(0, std::min(encoder_length, frames));

    void* encoder_data = nullptr;
    if (g_api->GetTensorMutableData(encoded.get(), &encoder_data) != nullptr || encoder_data == nullptr) {
        error = "Не удалось прочитать выход энкодера GigaAM RNN-T.";
        return false;
    }
    const float* encoder_values = static_cast<const float*>(encoder_data);

    std::vector<float> initial_hidden(kRnntPredHidden, 0.0f);
    std::vector<float> initial_cell(kRnntPredHidden, 0.0f);
    const int64_t state_shape[3] = {1, 1, kRnntPredHidden};

    OrtValue* hidden_raw = nullptr;
    OrtStatus* status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        initial_hidden.data(),
        initial_hidden.size() * sizeof(float),
        state_shape,
        3,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
        &hidden_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr state_hidden(hidden_raw);

    OrtValue* cell_raw = nullptr;
    status = g_api->CreateTensorWithDataAsOrtValue(
        g_memory_info,
        initial_cell.data(),
        initial_cell.size() * sizeof(float),
        state_shape,
        3,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
        &cell_raw);
    if (status != nullptr) {
        error = OrtError(status);
        return false;
    }
    OrtValuePtr state_cell(cell_raw);

    OrtValuePtr decoder_output;
    OrtValuePtr next_hidden;
    OrtValuePtr next_cell;
    if (!RunRnntDecoder(
            g_rnnt_blank,
            state_hidden.get(),
            state_cell.get(),
            decoder_output,
            next_hidden,
            next_cell,
            error)) {
        return false;
    }
    state_hidden = std::move(next_hidden);
    state_cell = std::move(next_cell);

    std::vector<int> tokens;
    for (int64_t frame = 0; frame < encoder_length; ++frame) {
        std::vector<float> encoder_step(static_cast<size_t>(hidden));
        for (int64_t index = 0; index < hidden; ++index) {
            encoder_step[static_cast<size_t>(index)] =
                encoder_values[static_cast<size_t>(index * frames + frame)];
        }

        for (int step = 0; step < kRnntMaxTokensPerStep; ++step) {
            OrtValuePtr joint;
            if (!RunRnntJoiner(encoder_step, decoder_output.get(), joint, error)) {
                return false;
            }

            const std::vector<int64_t> joint_shape = ShapeOf(joint.get());
            if (joint_shape.empty() || joint_shape.back() <= 0) {
                error = "Джойнер GigaAM RNN-T вернул тензор неожиданной формы.";
                return false;
            }

            const int64_t vocab_size = joint_shape.back();
            void* joint_data = nullptr;
            if (g_api->GetTensorMutableData(joint.get(), &joint_data) != nullptr || joint_data == nullptr) {
                error = "Не удалось прочитать выход джойнера GigaAM RNN-T.";
                return false;
            }

            const float* probabilities = static_cast<const float*>(joint_data);
            int best = 0;
            float best_value = probabilities[0];
            for (int64_t token = 1; token < vocab_size; ++token) {
                if (probabilities[token] > best_value) {
                    best_value = probabilities[token];
                    best = static_cast<int>(token);
                }
            }

            if (best == g_rnnt_blank) {
                break;
            }

            tokens.push_back(best);

            OrtValuePtr next_decoder;
            if (!RunRnntDecoder(
                    best,
                    state_hidden.get(),
                    state_cell.get(),
                    next_decoder,
                    next_hidden,
                    next_cell,
                    error)) {
                return false;
            }
            decoder_output = std::move(next_decoder);
            state_hidden = std::move(next_hidden);
            state_cell = std::move(next_cell);
        }
    }

    std::string raw;
    for (int token : tokens) {
        if (token >= 0 && static_cast<size_t>(token) < g_rnnt_vocab.size()) {
            raw += g_rnnt_vocab[static_cast<size_t>(token)];
        }
    }

    text = NormalizeText(ReplaceWordSeparator(raw));
    return true;
}

}  // namespace

extern "C" {

SONORA_API const char* sonora_gigaam_version() {
    return "sonora-gigaam/1.0 (ONNX Runtime)";
}

SONORA_API const char* sonora_gigaam_transcribe(
    const char* model_path,
    const char* preprocessor_path,
    const char* vocab_path,
    const char* audio_path) {
    try {
        if (model_path == nullptr || preprocessor_path == nullptr || vocab_path == nullptr || audio_path == nullptr) {
            return CopyToNative(ErrorJson("Не переданы пути к модели, препроцессору, словарю или аудиофайлу."));
        }

        std::lock_guard<std::mutex> lock(g_mutex);

        std::string error;
        if (!LoadRuntime(error)) {
            return CopyToNative(ErrorJson(error));
        }

        if (!EnsureSessions(model_path, preprocessor_path, vocab_path, error)) {
            return CopyToNative(ErrorJson(error));
        }

        std::vector<float> pcm;
        if (!DecodeAudio(audio_path, pcm, error)) {
            return CopyToNative(ErrorJson(error));
        }

        OrtValuePtr features;
        OrtValuePtr feature_lengths;
        if (!RunPreprocessor(pcm, features, feature_lengths, error)) {
            return CopyToNative(ErrorJson(error));
        }

        const std::vector<int64_t> feature_shape = ShapeOf(features.get());
        if (feature_shape.size() != 3) {
            return CopyToNative(ErrorJson("Препроцессор GigaAM вернул тензор неожиданной формы."));
        }

        void* length_data = nullptr;
        if (g_api->GetTensorMutableData(feature_lengths.get(), &length_data) != nullptr || length_data == nullptr) {
            return CopyToNative(ErrorJson("Не удалось прочитать длину признаков GigaAM."));
        }
        const int64_t feature_length = *static_cast<const int64_t*>(length_data);

        OrtValuePtr log_probs;
        if (!RunModel(features.get(), feature_lengths.get(), log_probs, error)) {
            return CopyToNative(ErrorJson(error));
        }

        const std::vector<int64_t> output_shape = ShapeOf(log_probs.get());
        if (output_shape.size() != 3 || output_shape[2] <= 0) {
            return CopyToNative(ErrorJson("Модель GigaAM вернула тензор неожиданной формы."));
        }

        void* output_data = nullptr;
        if (g_api->GetTensorMutableData(log_probs.get(), &output_data) != nullptr || output_data == nullptr) {
            return CopyToNative(ErrorJson("Не удалось прочитать результат GigaAM."));
        }

        const std::string text = DecodeCtc(
            static_cast<const float*>(output_data),
            output_shape[1],
            output_shape[2],
            feature_length);
        const int64_t duration_ms =
            static_cast<int64_t>((static_cast<double>(pcm.size()) * 1000.0) / static_cast<double>(kSampleRate));

        std::ostringstream json;
        json << "{\"ok\":true,\"text\":\"" << JsonEscape(text)
             << "\",\"duration_ms\":" << duration_ms
             << ",\"segments\":[{\"start_ms\":0,\"end_ms\":" << duration_ms
             << ",\"text\":\"" << JsonEscape(text)
             << "\",\"probability\":" << std::fixed << std::setprecision(4)
             << (duration_ms > 0 ? 1.0 : 0.0)
             << "}]}";

        return CopyToNative(json.str());
    } catch (const std::exception& ex) {
        return CopyToNative(ErrorJson(std::string("Ошибка GigaAM: ") + ex.what()));
    } catch (...) {
        return CopyToNative(ErrorJson("Неизвестная ошибка GigaAM."));
    }
}

SONORA_API const char* sonora_gigaam_rnnt_transcribe(
    const char* encoder_path,
    const char* decoder_path,
    const char* joint_path,
    const char* preprocessor_path,
    const char* vocab_path,
    const char* audio_path) {
    try {
        if (encoder_path == nullptr || decoder_path == nullptr || joint_path == nullptr ||
            preprocessor_path == nullptr || vocab_path == nullptr || audio_path == nullptr) {
            return CopyToNative(ErrorJson("Не переданы пути к моделям, препроцессору, словарю или аудиофайлу."));
        }

        std::lock_guard<std::mutex> lock(g_mutex);

        std::string error;
        if (!LoadRuntime(error)) {
            return CopyToNative(ErrorJson(error));
        }

        if (!EnsureRnntSessions(
                encoder_path,
                decoder_path,
                joint_path,
                preprocessor_path,
                vocab_path,
                error)) {
            return CopyToNative(ErrorJson(error));
        }

        std::vector<float> pcm;
        if (!DecodeAudio(audio_path, pcm, error)) {
            return CopyToNative(ErrorJson(error));
        }

        OrtValuePtr features;
        OrtValuePtr feature_lengths;
        if (!RunPreprocessorFor(g_rnnt_preprocessor, pcm, features, feature_lengths, error)) {
            return CopyToNative(ErrorJson(error));
        }

        const std::vector<int64_t> feature_shape = ShapeOf(features.get());
        if (feature_shape.size() != 3) {
            return CopyToNative(ErrorJson("Препроцессор GigaAM RNN-T вернул тензор неожиданной формы."));
        }

        std::string text;
        if (!RunRnnt(features.get(), feature_lengths.get(), text, error)) {
            return CopyToNative(ErrorJson(error));
        }

        const int64_t duration_ms =
            static_cast<int64_t>((static_cast<double>(pcm.size()) * 1000.0) / static_cast<double>(kSampleRate));

        std::ostringstream json;
        json << "{\"ok\":true,\"text\":\"" << JsonEscape(text)
             << "\",\"duration_ms\":" << duration_ms
             << ",\"segments\":[{\"start_ms\":0,\"end_ms\":" << duration_ms
             << ",\"text\":\"" << JsonEscape(text)
             << "\",\"probability\":" << std::fixed << std::setprecision(4)
             << (duration_ms > 0 ? 1.0 : 0.0)
             << "}]}";

        return CopyToNative(json.str());
    } catch (const std::exception& ex) {
        return CopyToNative(ErrorJson(std::string("Ошибка GigaAM RNN-T: ") + ex.what()));
    } catch (...) {
        return CopyToNative(ErrorJson("Неизвестная ошибка GigaAM RNN-T."));
    }
}

SONORA_API void sonora_gigaam_free_string(const char* value) {
    std::free(const_cast<char*>(value));
}

SONORA_API void sonora_gigaam_shutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    ReleaseSessions();
    ReleaseRnntSessions();

    if (g_api != nullptr) {
        if (g_memory_info != nullptr) {
            g_api->ReleaseMemoryInfo(g_memory_info);
            g_memory_info = nullptr;
        }
        if (g_options != nullptr) {
            g_api->ReleaseSessionOptions(g_options);
            g_options = nullptr;
        }
        if (g_env != nullptr) {
            g_api->ReleaseEnv(g_env);
            g_env = nullptr;
        }
    }

    if (g_runtime != nullptr) {
        FreeLibrary(g_runtime);
        g_runtime = nullptr;
    }
    g_api = nullptr;
}

}  // extern "C"
