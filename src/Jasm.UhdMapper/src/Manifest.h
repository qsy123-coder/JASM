#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace jasm::uhd {

// active-mod.json 的原生模型（schema v1；M3 再扩 components）。
struct ManifestTexture
{
    uint32_t hdHash = 0;
    bool hdHashValid = false;
    std::wstring ddsPath;
    std::vector<uint32_t> uhdHashes;
    std::wstring resourceSection;
    std::wstring overrideSection;
};

struct ManifestMod
{
    std::wstring name;
    std::wstring rootPath;
    std::vector<ManifestTexture> textures;
};

struct Manifest
{
    int schemaVersion = 0;
    std::wstring game;
    std::wstring role;
    std::wstring roleInternalName;
    std::vector<ManifestMod> mods;

    std::size_t TextureCount() const;
    std::size_t DdsCount() const;
};

enum class ManifestStatus
{
    Ok,
    Missing,
    Unreadable,
    InvalidJson,
    UnsupportedSchema,
};

ManifestStatus LoadManifest(const std::wstring& path, Manifest& out, std::string* error);

// 直接解析 JSON 文本（单测用；LoadManifest 内部也走这里）。
ManifestStatus ParseManifestJson(const std::string& json, Manifest& out, std::string* error);

// UTF-8 ↔ UTF-16（路径与日志用）。
std::wstring Utf8ToWide(const std::string& text);
std::string WideToUtf8(const std::wstring& text);

}  // namespace jasm::uhd
