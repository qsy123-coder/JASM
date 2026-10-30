// Jasm.UhdMapper —— 由提权助手在游戏进程 CREATE_SUSPENDED 时注入的 UHD 贴图映射 DLL。
//
// Phase 1（当前）：空壳。验证「注入时序 + 游戏能正常启动 + 能读到 JASM 写的 active-mod.json」。
// Phase 2 起在这里加 D3D11 hook（PSSetShaderResources / DrawIndexed）与 SRV 替换；
// Phase 3 加运行时配对与缓存。
//
// 注入路径见 GIMI_ModManager.Core.Helpers.ElevatorLaunchInjectProtocol 的命令 5。

#include <windows.h>

#include <cstdio>
#include <string>

namespace {

// 与托管侧 UhdActiveModManifest.CurrentSchemaVersion / 原生侧契约一起推进。
constexpr int kAbiVersion = 1;

constexpr wchar_t kRuntimeFolder[] = L"JASM\\UhdRuntime";
constexpr wchar_t kLogFileName[] = L"Jasm.UhdMapper.log";
constexpr wchar_t kManifestFileName[] = L"active-mod.json";

// DllMain 里保存；工作线程用它取自身模块路径（GetModuleFileNameW(hModule)）。
HMODULE g_module = nullptr;

std::wstring ReadEnvironment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0)
        return {};

    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0)
        return {};

    value.resize(written);
    return value;
}

bool DirectoryExists(const std::wstring& path) {
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

// 逐级创建目录（只用 kernel32，避免把 shell32 拖进被注入进程）。
void EnsureDirectory(const std::wstring& path) {
    if (path.empty() || DirectoryExists(path))
        return;

    const auto separator = path.find_last_of(L"\\/");
    if (separator != std::wstring::npos && separator > 2)
        EnsureDirectory(path.substr(0, separator));

    CreateDirectoryW(path.c_str(), nullptr);
}

std::wstring NowText() {
    SYSTEMTIME time{};
    GetLocalTime(&time);

    wchar_t buffer[64]{};
    swprintf_s(buffer, L"%04u-%02u-%02u %02u:%02u:%02u.%03u",
               time.wYear, time.wMonth, time.wDay,
               time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
    return buffer;
}

// 追加一行 UTF-8 文本。失败一律静默：这是诊断日志，不能因为写不了文件就影响游戏。
void AppendUtf8Line(const std::wstring& path, const std::wstring& text) {
    const HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                    nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;

    const int length = static_cast<int>(text.size());
    const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), length, nullptr, 0, nullptr, nullptr);
    if (size > 0) {
        std::string utf8(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), length, utf8.data(), size, nullptr, nullptr);

        DWORD written = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }

    CloseHandle(file);
}

DWORD WINAPI MapperWorker(LPVOID /*parameter*/) {
    wchar_t modulePath[4096]{};
    if (GetModuleFileNameW(g_module, modulePath, ARRAYSIZE(modulePath)) == 0)
        return 0;

    std::wstring runtimeDirectory = ReadEnvironment(L"LOCALAPPDATA");
    if (runtimeDirectory.empty())
        return 0;

    runtimeDirectory += L"\\";
    runtimeDirectory += kRuntimeFolder;
    EnsureDirectory(runtimeDirectory);

    // 空壳阶段的验收信号：JASM 的清单必须在 DLL 加载前就落盘。
    const std::wstring manifestPath = runtimeDirectory + L"\\" + kManifestFileName;
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    const bool manifestExists =
        GetFileAttributesExW(manifestPath.c_str(), GetFileExInfoStandard, &attributes) != 0;

    unsigned long long manifestBytes = 0;
    if (manifestExists) {
        manifestBytes = (static_cast<unsigned long long>(attributes.nFileSizeHigh) << 32)
                        | attributes.nFileSizeLow;
    }

    const std::wstring line = std::wstring(L"[") + NowText() + L"] injected pid="
                              + std::to_wstring(GetCurrentProcessId())
                              + L" abi=" + std::to_wstring(kAbiVersion)
                              + L" module=" + modulePath
                              + L" manifest=" + (manifestExists ? L"yes" : L"no")
                              + L" (" + std::to_wstring(manifestBytes) + L" bytes)\r\n";

    AppendUtf8Line(runtimeDirectory + L"\\" + kLogFileName, line);
    return 0;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID /*reserved*/) {
    switch (reason) {
        case DLL_PROCESS_ATTACH: {
            g_module = module;
            DisableThreadLibraryCalls(module);

            // 绝不在 DllMain 里做文件 IO：注入发生在挂起进程的加载期，
            // 在加载锁内碰文件/其它 DLL 有死锁风险。工作线程只做目录创建与日志。
            const HANDLE thread = CreateThread(nullptr, 0, MapperWorker, nullptr, 0, nullptr);
            if (thread != nullptr)
                CloseHandle(thread);
            break;
        }

        case DLL_PROCESS_DETACH:
            // Phase 2/3：在这里解 hook、释放贴图缓存。空壳阶段无需处理。
            break;

        default:
            break;
    }

    return TRUE;
}

// 给排错/未来握手用的 ABI 版本；注入成功与否不依赖导出（LoadLibraryW 本身即判据）。
extern "C" __declspec(dllexport) int JasmUhdMapperAbiVersion() {
    return kAbiVersion;
}
