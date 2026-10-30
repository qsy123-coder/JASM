#pragma once

#include <cstdint>
#include <string>

namespace jasm::uhd {

struct MapperConfig
{
    // mode=instrument 时只记录；mode=apply 才替换 SRV（M2）。
    bool applyMode = false;
    bool debugLog = false;
    bool readbackHash = false;

    uint64_t maxCacheMb = 1024;
    uint64_t maxTextureMb = 256;

    std::wstring runtimeDirectory;
    std::wstring manifestPath;
    std::wstring logPath;
    std::wstring iniPath;
    std::wstring disableFlagPath;
};

// 固定目录：%LOCALAPPDATA%\JASM\UhdRuntime（与托管侧 UhdActiveModStore 一致）。
MapperConfig BuildDefaultConfig();

// 读取 mapper.ini；不存在/读失败时保持默认值。
void LoadConfigFile(MapperConfig& config);

// disable.flag 是否存在（存在即停止替换）。
bool DisableFlagExists(const MapperConfig& config);

}  // namespace jasm::uhd
