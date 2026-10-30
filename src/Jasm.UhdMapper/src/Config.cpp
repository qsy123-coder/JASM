#include "Config.h"

#include <algorithm>
#include <cstdlib>
#include <fstream>
#include <vector>

#include <Windows.h>

namespace jasm::uhd {
namespace {

std::wstring ReadLocalAppData()
{
    DWORD required = GetEnvironmentVariableW(L"LOCALAPPDATA", nullptr, 0);
    if (required == 0)
        return {};

    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(L"LOCALAPPDATA", value.data(), required);
    if (written == 0)
        return {};

    value.resize(written);
    return value;
}

std::string Trim(const std::string& text)
{
    std::size_t begin = 0;
    while (begin < text.size() && (text[begin] == ' ' || text[begin] == '\t' || text[begin] == '\r'))
        ++begin;

    std::size_t end = text.size();
    while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t' || text[end - 1] == '\r'))
        --end;

    return text.substr(begin, end - begin);
}

bool ParseBool(const std::string& value)
{
    return value == "1" || value == "true" || value == "yes" || value == "on";
}

uint64_t ParseMb(const std::string& value, uint64_t fallback)
{
    char* end = nullptr;
    const unsigned long long parsed = std::strtoull(value.c_str(), &end, 10);
    if (end == nullptr || *end != '\0' || parsed == 0)
        return fallback;
    return static_cast<uint64_t>(parsed);
}

}  // namespace

MapperConfig BuildDefaultConfig()
{
    MapperConfig config;

    const std::wstring localAppData = ReadLocalAppData();
    if (!localAppData.empty())
        config.runtimeDirectory = localAppData + L"\\JASM\\UhdRuntime";

    config.manifestPath = config.runtimeDirectory + L"\\active-mod.json";
    config.logPath = config.runtimeDirectory + L"\\Jasm.UhdMapper.log";
    config.iniPath = config.runtimeDirectory + L"\\mapper.ini";
    config.disableFlagPath = config.runtimeDirectory + L"\\disable.flag";
    return config;
}

void LoadConfigFile(MapperConfig& config)
{
    if (config.iniPath.empty())
        return;

    std::ifstream file(config.iniPath.c_str());
    if (!file.is_open())
        return;

    std::string line;
    while (std::getline(file, line))
    {
        line = Trim(line);
        if (line.empty() || line[0] == '#' || line[0] == ';')
            continue;

        const auto separator = line.find('=');
        if (separator == std::string::npos)
            continue;

        const std::string key = Trim(line.substr(0, separator));
        const std::string value = Trim(line.substr(separator + 1));

        if (key == "mode")
            config.applyMode = value == "apply";
        else if (key == "log")
            config.debugLog = value == "debug";
        else if (key == "readback_hash")
            config.readbackHash = ParseBool(value);
        else if (key == "max_cache_mb")
            config.maxCacheMb = ParseMb(value, config.maxCacheMb);
        else if (key == "max_texture_mb")
            config.maxTextureMb = ParseMb(value, config.maxTextureMb);
    }
}

bool DisableFlagExists(const MapperConfig& config)
{
    if (config.disableFlagPath.empty())
        return false;

    return GetFileAttributesW(config.disableFlagPath.c_str()) != INVALID_FILE_ATTRIBUTES;
}

}  // namespace jasm::uhd
