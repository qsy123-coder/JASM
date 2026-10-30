#pragma once

#include <cstdint>
#include <shared_mutex>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <Windows.h>
#include <d3d11.h>

namespace jasm::uhd {

struct RegistryStats
{
    std::size_t textures = 0;
    std::size_t srvs = 0;
    std::size_t hashedTextures = 0;
    std::size_t unknownTextures = 0;
};

// texture → CRC32C hash、SRV → texture/hash 的映射。
// 只存裸指针元数据，不持有 COM 引用：游戏负责对象生命周期。
class TextureRegistry
{
public:
    static TextureRegistry& Instance();

    void OnTextureCreated(ID3D11Texture2D* texture, const D3D11_TEXTURE2D_DESC& desc,
                          const D3D11_SUBRESOURCE_DATA* initialData);

    void RememberSrv(ID3D11ShaderResourceView* srv, ID3D11Resource* resource);

    bool TryGetSrvHash(ID3D11ShaderResourceView* srv, uint32_t* hash, bool* isReplacement) const;

    bool TryGetTextureHash(ID3D11Resource* resource, uint32_t* hash) const;

    bool TryGetTextureDesc(ID3D11Resource* resource, D3D11_TEXTURE2D_DESC* desc) const;

    // 纹理创建后通过 Map/Unmap、UpdateSubresource、Copy* 才拿到内容时补写 hash。
    // desc 可选：更新路径能拿到实时描述时传进来，观测清单里就有格式/尺寸。
    void SetTextureHash(ID3D11Resource* resource, uint32_t hash,
                        const D3D11_TEXTURE2D_DESC* desc = nullptr);

    // M1 诊断：导出所有算过 hash 的纹理（去重，最多 20 万条）。
    struct ObservedTexture
    {
        uint32_t hash = 0;
        uint32_t format = 0;
        uint32_t width = 0;
        uint32_t height = 0;
    };
    std::vector<ObservedTexture> SnapshotObserved(std::size_t max) const;
    std::size_t ObservedCount() const;
    void CopyTextureHash(ID3D11Resource* destination, ID3D11Resource* source);

    bool IsSrvRegistered(ID3D11ShaderResourceView* srv) const;

    uint64_t UpdatedHashCount() const { return _updatedHashes; }

    void MarkReplacement(ID3D11ShaderResourceView* srv, bool replacement);

    RegistryStats GetStats() const;
    void Clear();

private:
    TextureRegistry() = default;

    struct TextureEntry
    {
        uint32_t hash = 0;
        bool valid = false;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        UINT width = 0;
        UINT height = 0;
        UINT mips = 0;
        UINT arraySize = 0;
    };

    struct SrvEntry
    {
        ID3D11Resource* resource = nullptr;
        uint32_t hash = 0;
        bool valid = false;
        bool replacement = false;
    };

    static constexpr std::size_t kMaxEntries = 200000;
    static constexpr std::size_t kMaxObserved = 200000;

    void RecordObservedLocked(uint32_t hash, const TextureEntry& entry);

    mutable std::shared_mutex _mutex;
    std::unordered_map<ID3D11Texture2D*, TextureEntry> _textures;
    std::unordered_map<ID3D11ShaderResourceView*, SrvEntry> _srvs;
    std::size_t _hashed = 0;
    std::size_t _unknown = 0;
    uint64_t _updatedHashes = 0;
    std::unordered_set<uint32_t> _observedSet;
    std::vector<ObservedTexture> _observed;
};

}  // namespace jasm::uhd
