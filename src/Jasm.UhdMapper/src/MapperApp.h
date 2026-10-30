#pragma once

#include <atomic>
#include <memory>
#include <vector>

#include <Windows.h>

#include "AliasTable.h"
#include "Config.h"
#include "Manifest.h"

namespace jasm::uhd {

// DLL 的宿主：DllMain 只负责拉起工作线程，所有解析/钩子/日志都在这里。
class MapperApp
{
public:
    static MapperApp& Instance();

    void Start();
    void Stop();

private:
    MapperApp() = default;

    static DWORD WINAPI ThreadEntry(LPVOID parameter);
    void WorkerMain();

    void CreateReadyEvent();
    void SignalReady();

    bool ReloadManifest();
    void UpdateDisableFlag();
    void LogSummary();

    MapperConfig _config;
    std::unique_ptr<AliasTable> _alias;
    // 旧别名表永不释放：hooks 里可能还持有指针，只做整体替换。
    std::vector<std::unique_ptr<AliasTable>> _retiredAliases;
    bool _manifestLoaded = false;
    bool _disableLogged = false;
    HANDLE _readyEvent = nullptr;
    FILETIME _manifestWriteTime{};
    std::atomic<bool> _running{false};
    bool _started = false;
};

}  // namespace jasm::uhd
