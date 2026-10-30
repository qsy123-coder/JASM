#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>
#include <unordered_set>

#include "Manifest.h"

namespace jasm::uhd {

struct AliasTarget
{
    std::wstring ddsPath;
    std::wstring modRoot;
    uint32_t hdHash = 0;
    std::wstring modName;
};

// UHD hash → mod DDS 的索引。构建时做冲突判定：
//   · 与任何 mod 的 hdHash 相同 → 跳过（WWMI 已按 HD hash 处理）；
//   · 同一 UHD hash 指向不同 DDS → 标 ambiguous，永不替换；
//   · 路径不在 mod root 下 / 非 .dds / 无 ddsPath → 跳过。
class AliasTable
{
public:
    void Build(const Manifest& manifest);

    bool TryGet(uint32_t uhdHash, AliasTarget* target) const;

    // 是否是 manifest 里任何 mod 的 HD hash（仪器阶段用来区分 hd/uhd 命中）。
    bool IsHdHash(uint32_t hash) const { return _hdHashes.count(hash) != 0; }

    std::size_t Count() const { return _map.size(); }
    std::size_t AmbiguousCount() const { return _ambiguous.size(); }
    std::size_t SkippedCount() const { return _skipped; }
    std::size_t TextureCount() const { return _textures; }

    std::string Summary() const;

private:
    std::unordered_map<uint32_t, AliasTarget> _map;
    std::unordered_set<uint32_t> _ambiguous;
    std::unordered_set<uint32_t> _hdHashes;
    std::size_t _skipped = 0;
    std::size_t _textures = 0;
};

}  // namespace jasm::uhd
