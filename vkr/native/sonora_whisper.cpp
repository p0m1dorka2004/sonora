#define MA_NO_DEVICE_IO
#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"
#include "whisper.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#define SONORA_API __declspec(dllexport)
#else
#define SONORA_API __attribute__((visibility("default")))
#endif

namespace {

std::mutex g_mutex;
whisper_context* g_context = nullptr;
std::string g_model_path;

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

bool EnsureContext(const std::string& model_path, std::string& error) {
    if (g_context != nullptr && g_model_path == model_path) return true;

    if (g_context != nullptr) {
        whisper_free(g_context);
        g_context = nullptr;
        g_model_path.clear();
    }

    whisper_context_params params = whisper_context_default_params();
    params.use_gpu = false;
    g_context = whisper_init_from_file_with_params(model_path.c_str(), params);
    if (g_context == nullptr) {
        error = "Не удалось загрузить модель whisper.cpp: " + model_path;
        return false;
    }

    g_model_path = model_path;
    return true;
}

bool DecodeAudio(const std::string& audio_path, std::vector<float>& pcm, std::string& error) {
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 1, 16000);
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
            if (pcm.size() > 16000ULL * 60ULL * 30ULL) {
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

}  // namespace

extern "C" {

SONORA_API const char* sonora_version() {
    return "sonora-whisper/2.0 (whisper.cpp v1.9.5)";
}

SONORA_API const char* sonora_transcribe(const char* model_path, const char* audio_path) {
    try {
        if (model_path == nullptr || audio_path == nullptr) {
            return CopyToNative(ErrorJson("Не переданы пути к модели или аудиофайлу."));
        }

        std::lock_guard<std::mutex> lock(g_mutex);

        std::string error;
        if (!EnsureContext(model_path, error)) {
            return CopyToNative(ErrorJson(error));
        }

        std::vector<float> pcm;
        if (!DecodeAudio(audio_path, pcm, error)) {
            return CopyToNative(ErrorJson(error));
        }

        whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);

        const int threads = std::max(2, std::min(8, static_cast<int>(std::thread::hardware_concurrency()) / 2));
        params.n_threads = threads;
        params.translate = false;
        params.no_context = true;
        params.single_segment = false;
        params.print_progress = false;
        params.print_realtime = false;
        params.print_timestamps = false;
        params.print_special = false;
        params.language = "ru";

        if (whisper_full(g_context, params, pcm.data(), static_cast<int>(pcm.size())) != 0) {
            return CopyToNative(ErrorJson("whisper.cpp не смог обработать аудиофайл."));
        }

        const int segment_count = whisper_full_n_segments(g_context);
        std::string full_text;
        std::ostringstream json;
        json << "{\"ok\":true,\"text\":\"";

        std::ostringstream segment_json;
        segment_json << "[";

        for (int i = 0; i < segment_count; ++i) {
            const char* raw_text = whisper_full_get_segment_text(g_context, i);
            std::string text = raw_text != nullptr ? raw_text : "";
            if (!text.empty() && !full_text.empty()) full_text += " ";
            full_text += text;

            double probability = 0.0;
            const int token_count = whisper_full_n_tokens(g_context, i);
            if (token_count > 0) {
                double sum = 0.0;
                for (int t = 0; t < token_count; ++t) {
                    sum += whisper_full_get_token_p(g_context, i, t);
                }
                probability = sum / token_count;
            }

            const int64_t start_ms = whisper_full_get_segment_t0(g_context, i) * 10;
            const int64_t end_ms = whisper_full_get_segment_t1(g_context, i) * 10;

            if (i > 0) segment_json << ",";
            segment_json << "{\"start_ms\":" << start_ms
                         << ",\"end_ms\":" << end_ms
                         << ",\"text\":\"" << JsonEscape(text)
                         << "\",\"probability\":" << std::fixed << std::setprecision(4)
                         << probability << "}";
        }

        segment_json << "]";
        const int64_t duration_ms = static_cast<int64_t>((static_cast<double>(pcm.size()) * 1000.0) / 16000.0);

        json << JsonEscape(full_text)
             << "\",\"duration_ms\":" << duration_ms
             << ",\"segments\":" << segment_json.str()
             << "}";

        return CopyToNative(json.str());
    } catch (const std::exception& ex) {
        return CopyToNative(ErrorJson(std::string("Ошибка whisper.cpp: ") + ex.what()));
    } catch (...) {
        return CopyToNative(ErrorJson("Неизвестная ошибка whisper.cpp."));
    }
}

SONORA_API void sonora_free_string(const char* value) {
    std::free(const_cast<char*>(value));
}

SONORA_API void sonora_shutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_context != nullptr) {
        whisper_free(g_context);
        g_context = nullptr;
    }
    g_model_path.clear();
}

}  // extern "C"
