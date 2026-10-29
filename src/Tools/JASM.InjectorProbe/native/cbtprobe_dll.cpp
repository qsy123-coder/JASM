// cbtprobe.dll —— 注入探针（Phase A2）用的最小"可被 WH_CBT 钩子加载"的 DLL。
//
// 它的唯一职责：证明"全局 CBT 钩子能把一个 DLL 送进目标进程"。
// 不碰游戏、不做任何逻辑，DllMain 只写一行日志并置一个命名事件供探针核对。
//
// 构建：见同目录 CMakeLists.txt。本机没有 MSVC，需要 VS Build Tools 或 CI。

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// 探针用它判断"DLL 真的被映射进来了"；命名与其他全局对象保持一致的前缀风格。
static const wchar_t* kLoadedEventName = L"Local\\JasmInjectorProbeLoaded";

static void WriteProbeLog(const wchar_t* phase)
{
    wchar_t tempPath[MAX_PATH] = {};
    if (GetTempPathW(MAX_PATH, tempPath) == 0)
    {
        return;
    }

    wchar_t logPath[MAX_PATH] = {};
    if (swprintf_s(logPath, L"%sjasm-cbtprobe.log", tempPath) < 0)
    {
        return;
    }

    // 追加：多个进程会同时写，用 FILE_APPEND_DATA 让小行尽量不交错
    HANDLE file = CreateFileW(logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    SYSTEMTIME now = {};
    GetLocalTime(&now);
    wchar_t line[512] = {};
    int written = swprintf_s(line, L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] pid=%lu %s\r\n",
                             now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
                             now.wMilliseconds, GetCurrentProcessId(), phase);
    if (written > 0)
    {
        DWORD bytesWritten = 0;
        WriteFile(file, line, (DWORD)(written * sizeof(wchar_t)), &bytesWritten, nullptr);
    }

    CloseHandle(file);
}

// 导出：WH_CBT 钩子过程。探针安装钩子时把它当成回调；只要它被调用，
// 就说明 Windows 已经把本 DLL 映射进了触发 CBT 事件的进程。
extern "C" __declspec(dllexport) LRESULT CALLBACK CBTProc(int code, WPARAM wParam, LPARAM lParam)
{
    // 只在 HCBT_CREATEWND 这类"进程正在建窗口"的时机记一次，避免刷屏
    static LONG reported = 0;
    if (code == HCBT_CREATEWND && InterlockedCompareExchange(&reported, 1, 0) == 0)
    {
        WriteProbeLog(L"CBTProc called (HCBT_CREATEWND)");

        HANDLE eventHandle = CreateEventW(nullptr, TRUE, TRUE, kLoadedEventName);
        if (eventHandle != nullptr)
        {
            CloseHandle(eventHandle);
        }
    }

    return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        WriteProbeLog(L"DLL_PROCESS_ATTACH (hook injection landed)");
        break;
    case DLL_PROCESS_DETACH:
        break;
    default:
        break;
    }

    return TRUE;
}