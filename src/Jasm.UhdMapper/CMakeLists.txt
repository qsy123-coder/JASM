# Jasm.UhdMapper —— 注入到鸣潮进程的 UHD 贴图映射 DLL（PRD Phase 2）。
#
# 只为 Windows x64 + MSVC 构建；本机没有 MSVC，构建交给
# .github/workflows/uhd-mapper.yml，产物在 build/Release/Jasm.UhdMapper.dll，
# 主程序 csproj 用 Exists 条件把它内嵌进 exe。
#
# 里程碑：
#   M1 仪表（默认 mode=instrument）：钩 D3D11 创建/绑定，只算 hash 并写日志，不替换。
#   M2 对 hash 型 mod 替换 SRV（需要 DirectXTex 读 DDS，选项 JASM_UHD_MAPPER_WITH_DDS=ON）。
#   M3 RabbitFX/组件式 mod + 逐 draw 规则。

cmake_minimum_required(VERSION 3.20)
project(JasmUhdMapper VERSION 0.2.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT MSVC)
    message(FATAL_ERROR "Jasm.UhdMapper 只支持 MSVC / Windows x64")
endif()

# 注入进游戏进程后不能再依赖 VC 运行库 DLL：静态链接 CRT（Debug 也静态）。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

option(JASM_UHD_MAPPER_BUILD_TESTS "Build native unit tests" OFF)
option(JASM_UHD_MAPPER_WITH_DDS "Link DirectXTex for DDS replacement (M2)" ON)

# ── MinHook（BSD-2-Clause）──────────────────────────────────────────────
# 只用来挂 d3d11.dll 的两个创建导出；游戏自己的 COM vtable 用指针替换，不经过 MinHook。
include(FetchContent)
FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    # 固定 tag，避免上游改动让 CI 结果漂移
    GIT_TAG v1.3.3
    GIT_SHALLOW TRUE
)
# v1.3.3 仓库里没有 CMakeLists（只有 VS 工程），MakeAvailable 只会把源码拉下来，
# 不会生成 target —— 这里手动建静态库。
FetchContent_MakeAvailable(minhook)

add_library(minhook STATIC
    ${minhook_SOURCE_DIR}/src/buffer.c
    ${minhook_SOURCE_DIR}/src/hook.c
    ${minhook_SOURCE_DIR}/src/trampoline.c
    ${minhook_SOURCE_DIR}/src/hde/hde64.c
)
target_include_directories(minhook PUBLIC ${minhook_SOURCE_DIR}/include)
target_compile_definitions(minhook PUBLIC MH_STATIC)

# ── 可从测试直接链接的纯逻辑 ───────────────────────────────────────────
add_library(jasm_mapper_core STATIC
    src/Crc32c.cpp
    src/TextureHash.cpp
    src/Json.cpp
    src/Manifest.cpp
    src/AliasTable.cpp
    src/PathSafety.cpp
    src/Config.cpp
    src/Log.cpp
)

target_include_directories(jasm_mapper_core PUBLIC src)
target_compile_definitions(jasm_mapper_core PUBLIC
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
)
target_compile_options(jasm_mapper_core PRIVATE /utf-8 /W4 /permissive- /EHsc)
target_link_libraries(jasm_mapper_core PUBLIC d3d11 dxguid kernel32)

# ── 注入的 DLL ─────────────────────────────────────────────────────────
set(JASM_UHD_MAPPER_SOURCES
    src/dllmain.cpp
    src/MapperApp.cpp
    src/D3D11Hooks.cpp
    src/TextureRegistry.cpp
)

if(JASM_UHD_MAPPER_WITH_DDS)
    list(APPEND JASM_UHD_MAPPER_SOURCES src/DdsCache.cpp)
endif()

add_library(JasmUhdMapper SHARED ${JASM_UHD_MAPPER_SOURCES})

set_target_properties(JasmUhdMapper PROPERTIES
    OUTPUT_NAME "Jasm.UhdMapper"
    PREFIX ""
    # 固定输出目录：主程序 csproj 的 Exists 条件依赖这个路径
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/Release"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Release"
)

target_compile_definitions(JasmUhdMapper PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    MH_STATIC
)
target_compile_options(JasmUhdMapper PRIVATE /utf-8 /W4 /permissive- /EHsc)
target_link_libraries(JasmUhdMapper PRIVATE jasm_mapper_core minhook)

if(JASM_UHD_MAPPER_WITH_DDS)
    # M2 使用：DirectXTex（MIT），只用来解析 DDS；纹理仍由我们自己 CreateTexture2D。
    # 固定 tag；关掉工具/样例/DX11/DX12 扩展，避免引入 fxc 与额外依赖。
    FetchContent_Declare(
        directxtex
        GIT_REPOSITORY https://github.com/microsoft/DirectXTex.git
        GIT_TAG oct2024
        GIT_SHALLOW TRUE
    )
    set(BUILD_TOOLS OFF CACHE BOOL "" FORCE)
    set(BUILD_SAMPLE OFF CACHE BOOL "" FORCE)
    set(BUILD_DX11 OFF CACHE BOOL "" FORCE)
    set(BUILD_DX12 OFF CACHE BOOL "" FORCE)
    set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(directxtex)
    target_compile_definitions(JasmUhdMapper PRIVATE JASM_UHD_MAPPER_WITH_DDS)
    target_link_libraries(JasmUhdMapper PRIVATE DirectXTex)
endif()

# ── 原生单测（CI 跑 ctest）───────────────────────────────────────────────
if(JASM_UHD_MAPPER_BUILD_TESTS)
    enable_testing()
    add_executable(JasmUhdMapperTests tests/TestMain.cpp)
    target_link_libraries(JasmUhdMapperTests PRIVATE jasm_mapper_core)
    target_compile_options(JasmUhdMapperTests PRIVATE /utf-8 /EHsc)
    add_test(NAME JasmUhdMapperTests COMMAND JasmUhdMapperTests)
endif()
