cmake_minimum_required(VERSION 3.20)

# 注入探针（Phase A2）用的最小 DLL。
# 必须编成 x64：探针本身是 x64，32 位 DLL 进不了 64 位目标。
project(cbtprobe LANGUAGES CXX)

add_library(cbtprobe SHARED cbtprobe_dll.cpp)

set_target_properties(cbtprobe PROPERTIES
    OUTPUT_NAME "cbtprobe"
    PREFIX ""
    # 只要导出 CBTProc；其余符号一律不导出，避免 ABI 噪音
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

# 静态链接 C 运行库：产物是单文件，拷到别的机器也能用
set_property(TARGET cbtprobe PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")

target_compile_definitions(cbtprobe PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

target_compile_options(cbtprobe PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4 /permissive- /EHsc>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
