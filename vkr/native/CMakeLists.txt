cmake_minimum_required(VERSION 3.20)
project(sonora_whisper LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)

add_subdirectory(whisper.cpp)

add_library(sonora_whisper SHARED sonora_whisper.cpp)
target_include_directories(sonora_whisper PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/whisper.cpp/include
    ${CMAKE_CURRENT_SOURCE_DIR}/whisper.cpp/ggml/include
    ${CMAKE_CURRENT_SOURCE_DIR}/third_party
)
target_link_libraries(sonora_whisper PRIVATE whisper ggml)

set_target_properties(sonora_whisper PROPERTIES
    PREFIX ""
    OUTPUT_NAME "sonora_whisper"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

if (MINGW)
    target_link_options(sonora_whisper PRIVATE
        -static
        -static-libgcc
        -static-libstdc++
    )
endif()
