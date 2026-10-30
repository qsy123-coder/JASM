#include "MapperApp.h"

#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>

#include "D3D11Hooks.h"
#include "Log.h"
#include "TextureHash.h"
#include "TextureRegistry.h"

#ifdef JASM_UHD_MAPPER_WITH_DDS
#include "DdsCache.h"
#endif

namespace jasm::uhd {
namespace {

// 与 ElevatorLaunchInjectProtocol 的就绪事件名保持一致（Local\Jasm.UhdMapper.Ready.<pid>）。
constexpr wchar_t kReadyEventPrefix[] = L"Local\\Jasm.UhdMapper.Ready.";
constexpr DWORD kLoopSleepMs = 2000;
constexpr auto kSummaryInterval = std::chrono::seconds(10);

bool ReadWriteTime(const std::wstring& path, FILETIME* value)
{
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
        return false;

    *value = attributes.ftLastWriteTime;
    return true;
}

}  // namespace

MapperApp& MapperApp::Instance()
{
    static MapperApp instance;
    return instance;
}

void MapperApp::Start()
{
    if (_started)
        return;

    _started = true;
    // 不用 std::thread：DllMain 里创建线程时走 Win32 更稳。
    HANDLE thread = CreateThread(nullptr, 0, &MapperApp::ThreadEntry, this, 0, nullptr);
    if (thread != nullptr)
        CloseHandle(thread);
}

void MapperApp::Stop()
{
    _running.store(false, std::memory_order_release);
}

DWORD WINAPI MapperApp::ThreadEntry(LPVOID parameter)
{
    static_cast<MapperApp*>(parameter)->WorkerMain();
    return 0;
}

void MapperApp::CreateReadyEvent()
{
    const std::wstring name =
        std::wstring(kReadyEventPrefix) + std::to_wstring(GetCurrentProcessId());
    _readyEvent = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
}

void MapperApp::SignalReady()
{
    if (_readyEvent != nullptr)
        SetEvent(_readyEvent);
}

bool MapperApp::ReloadManifest()
{
    Manifest manifest;
    std::string error;
    const ManifestStatus status = LoadManifest(_config.manifestPath, manifest, &error);

    if (status != ManifestStatus::Ok)
    {
        LogWrite(status == ManifestStatus::Missing ? LogLevel::Info : LogLevel::Warn,
                 "manifest status=%d path=%s error=%s", static_cast<int>(status),
                 WideToUtf8(_config.manifestPath).c_str(), error.c_str());
        _manifestLoaded = false;
        SetAliasTableForHooks(nullptr);
        return false;
    }

    auto table = std::make_unique<AliasTable>();
    table->Build(manifest);

    LogWrite(LogLevel::Info, "manifest schema=%d role=%s mods=%zu textures=%zu dds=%zu",
             manifest.schemaVersion, WideToUtf8(manifest.role).c_str(), manifest.mods.size(),
             manifest.TextureCount(), manifest.DdsCount());
    LogWrite(LogLevel::Info, "alias table: %s", table->Summary().c_str());

    if (_alias)
        _retiredAliases.push_back(std::move(_alias));

    _alias = std::move(table);
    SetAliasTableForHooks(_alias.get());
    _manifestLoaded = true;

    FILETIME writeTime{};
    if (ReadWriteTime(_config.manifestPath, &writeTime))
        _manifestWriteTime = writeTime;

    return true;
}

void MapperApp::UpdateDisableFlag()
{
    const bool disabled = DisableFlagExists(_config);
    if (disabled)
    {
        if (!_disableLogged)
        {
            LogWrite(LogLevel::Warn, "disable.flag present; substitution disabled");
            _disableLogged = true;
        }
        SetApplyMode(false);
        return;
    }

    if (_disableLogged)
    {
        LogWrite(LogLevel::Info, "disable.flag removed; restored configured mode");
        _disableLogged = false;
    }

    SetApplyMode(_config.applyMode && _manifestLoaded);
}

void MapperApp::WriteObservedTextures()
{
    const auto observed = TextureRegistry::Instance().SnapshotObserved(0);
    if (observed.size() == _lastObservedCount)
        return;

    _lastObservedCount = observed.size();

    const std::wstring path = _config.runtimeDirectory + L"\\observed-textures.tsv";
    std::ofstream file(std::filesystem::path(path),
                       std::ios::out | std::ios::binary | std::ios::trunc);
    if (!file.is_open())
        return;

    file << "hash\tformat\twidth\theight\n";
    for (const auto& item : observed)
        file << HashToHex(item.hash) << '\t' << item.format << '\t' << item.width << '\t'
             << item.height << '\n';
}

void MapperApp::LogSummary()
{
    const HookCounters counters = GetHookCounters();
    LogWrite(LogLevel::Info,
             "summary binds=%llu hashed=%llu unknown=%llu hd=%llu uhd=%llu substitutions=%llu "
             "failures=%llu updatedHashes=%llu",
             static_cast<unsigned long long>(counters.binds),
             static_cast<unsigned long long>(counters.hashedBinds),
             static_cast<unsigned long long>(counters.unknownBinds),
             static_cast<unsigned long long>(counters.hdBinds),
             static_cast<unsigned long long>(counters.uhdBinds),
             static_cast<unsigned long long>(counters.substitutions),
             static_cast<unsigned long long>(counters.failures),
             static_cast<unsigned long long>(counters.updatedHashes));
}

void MapperApp::WorkerMain()
{
    // 先建事件：Elevator 会重试打开它；信号在钩子装好之后才发。
    CreateReadyEvent();

    _config = BuildDefaultConfig();
    LoadConfigFile(_config);

#ifdef JASM_UHD_MAPPER_WITH_DDS
    DdsCache::Instance().SetLimits(_config.maxCacheMb * 1024ull * 1024ull,
                                   _config.maxTextureMb * 1024ull * 1024ull);
#endif

    LogInit(_config.logPath, _config.debugLog);
    LogWrite(LogLevel::Info, "mapper start pid=%lu abi=2 mode=%s runtime=%s",
             GetCurrentProcessId(), _config.applyMode ? "apply" : "instrument",
             WideToUtf8(_config.runtimeDirectory).c_str());

    const bool manifestOk = ReloadManifest();
    SetApplyMode(_config.applyMode && manifestOk);

    const bool hooksInstalled = InstallD3D11Hooks();
    LogWrite(hooksInstalled ? LogLevel::Info : LogLevel::Warn, "hooks installed=%d",
             hooksInstalled ? 1 : 0);

    // 钩子（或失败结论）就绪后再放行游戏主线程。
    SignalReady();

    _running.store(true, std::memory_order_release);
    auto lastSummary = std::chrono::steady_clock::now();

    while (_running.load(std::memory_order_acquire))
    {
        Sleep(kLoopSleepMs);

        UpdateDisableFlag();

        FILETIME writeTime{};
        if (ReadWriteTime(_config.manifestPath, &writeTime))
        {
            if (CompareFileTime(&writeTime, &_manifestWriteTime) != 0)
            {
                LogWrite(LogLevel::Info, "manifest changed; reloading");
                const bool ok = ReloadManifest();
                SetApplyMode(_config.applyMode && ok);
            }
        }
        else if (_manifestLoaded)
        {
            // 清单被删除：停止替换，保留日志里的 instrument 记录。
            _manifestLoaded = false;
            SetAliasTableForHooks(nullptr);
            SetApplyMode(false);
            LogWrite(LogLevel::Warn, "manifest disappeared; substitution disabled");
        }

        const auto now = std::chrono::steady_clock::now();
        if (now - lastSummary >= kSummaryInterval)
        {
            LogSummary();
            WriteObservedTextures();
            lastSummary = now;
        }
    }

    SetApplyMode(false);
    LogWrite(LogLevel::Info, "mapper stopping");
    LogFlush();
    LogShutdown();
}

}  // namespace jasm::uhd
