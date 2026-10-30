#include "AliasTable.h"

#include <cstdio>
#include <cwchar>
#include <string.h>

#include "PathSafety.h"

namespace jasm::uhd {

void AliasTable::Build(const Manifest& manifest)
{
    _map.clear();
    _ambiguous.clear();
    _hdHashes.clear();
    _skipped = 0;
    _textures = 0;

    // 先收集所有 mod 的 HD hash：UHD hash 若等于任何一个 HD hash，交给 WWMI 原路径处理。
    for (const auto& mod : manifest.mods)
    {
        for (const auto& texture : mod.textures)
        {
            if (texture.hdHashValid)
                _hdHashes.insert(texture.hdHash);
        }
    }

    for (const auto& mod : manifest.mods)
    {
        for (const auto& texture : mod.textures)
        {
            ++_textures;

            if (texture.ddsPath.empty() || !HasDdsExtension(texture.ddsPath)
                || !IsUnderDirectory(texture.ddsPath, mod.rootPath))
            {
                ++_skipped;
                continue;
            }

            for (uint32_t uhdHash : texture.uhdHashes)
            {
                if (_hdHashes.count(uhdHash) != 0)
                {
                    ++_skipped;
                    continue;
                }

                if (_ambiguous.count(uhdHash) != 0)
                    continue;

                auto existing = _map.find(uhdHash);
                if (existing == _map.end())
                {
                    _map.emplace(uhdHash, AliasTarget{texture.ddsPath, mod.rootPath,
                                                      texture.hdHash, mod.name});
                    continue;
                }

                if (_wcsicmp(existing->second.ddsPath.c_str(), texture.ddsPath.c_str()) == 0)
                    continue;

                // 同一 UHD hash 指向不同 DDS：无法判定该用哪个，永久跳过。
                _map.erase(existing);
                _ambiguous.insert(uhdHash);
            }
        }
    }
}

bool AliasTable::TryGet(uint32_t uhdHash, AliasTarget* target) const
{
    if (target == nullptr || _ambiguous.count(uhdHash) != 0)
        return false;

    const auto it = _map.find(uhdHash);
    if (it == _map.end())
        return false;

    *target = it->second;
    return true;
}

std::string AliasTable::Summary() const
{
    char buffer[256] = {};
    std::snprintf(buffer, sizeof(buffer),
                  "aliases=%zu ambiguous=%zu skipped=%zu textures=%zu",
                  _map.size(), _ambiguous.size(), _skipped, _textures);
    return buffer;
}

}  // namespace jasm::uhd
