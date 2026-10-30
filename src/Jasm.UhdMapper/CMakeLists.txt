# Jasm.UhdMapper —— 注入到鸣潮进程的 UHD 贴图映射 DLL（PRD Phase 2）。
#
# 只为 Windows x64 + MSVC 构建；本机通常没有 MSVC，构建交给
# .github/workflows/uhd-mapper.yml，产物在 build/Release/Jasm.UhdMapper.dll，
# 主程序 csproj 用 Exists 条件把它内嵌进 exe。
#
# 当前是 Phase 1 空壳：只验证「注入时序 + 能读到 active-mod.json」，
# D3D11 hook 与 SRV 替换在 Phase 2 实现。

cmake_minimum_required(VERSION 3.20)
project(JasmUhdMapper VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT MSVC)
    message(FATAL_ERROR "Jasm.UhdMapper 只支持 MSVC / Windows x64")
endif()

# 注入进游戏进程后不能再依赖 VC 运行库 DLL：静态链接 CRT。
# Debug 也静态（否则调试版还要找 vcruntime140d.dll）。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(JasmUhdMapper SHARED src/dllmain.cpp)

# 文件名必须是 Jasm.UhdMapper.dll：提权助手按这个名字 + 父目录 UhdRuntime 校验。
set_target_properties(JasmUhdMapper PROPERTIES
    OUTPUT_NAME "Jasm.UhdMapper"
    PREFIX ""
)

target_compile_definitions(JasmUhdMapper PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(JasmUhdMapper PRIVATE /utf-8 /W4 /permissive-)

# 只用 kernel32：注入线程里别再拖进 shell32 等额外 DLL。
target_link_libraries(JasmUhdMapper PRIVATE kernel32)
