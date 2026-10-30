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

// texture → data hash / 最终 hash（含 desc）与描述；SRV → texture/hash 的映射。
// 只存裸指针元数据，不持有 COM 引用：游戏负责对象生命周期。
//
// 最终 hash 必须与 3DMigoto 一致：CRC32C(desc 字节, seed=data_hash)，desc 使用
// **创建时的原始描述**（MipLevels 可能是 0，由 DX 生成），不能用 GetDesc 的结果覆盖。
class TextureRegistry
{
public:
    static TextureRegistry& Instance();

    void OnTextureCreated(ID3D11Texture2D* texture, const D3D11_TEXTURE2D_DESC& desc,
                          const D3D11_SUBRESOURCE_DATA* initialData, UINT screenWidth,
                          UINT screenHeight);

    void RememberSrv(ID3D11ShaderResourceView* srv, ID3D11Resource* resource);

    bool TryGetSrvHash(ID3D11ShaderResourceView* srv, uint32_t* hash, bool* isReplacement) const;

    bool TryGetTextureHash(ID3D11Resource* resource, uint32_t* hash) const;

    bool TryGetTextureDesc(ID3D11Resource* resource, D3D11_TEXTURE2D_DESC* desc) const;

    // 更新路径（Map/Unmap、UpdateSubresource、Copy*）拿到数据后写入 data hash；
    // 最终 hash 由注册表用原始描述 + 屏幕分辨率计算。desc 只在纹理未登记时用作回退。
    void SetTextureDataHash(ID3D11Resource* resource, uint32_t dataHash,
                            const D3D11_TEXTURE2D_DESC* desc, UINT screenWidth, UINT screenHeight);

    // 3DMigoto PropagateResourceHash：拷贝 data hash，最终 hash 用目标纹理的描述重算。
    void CopyTextureDataHash(ID3D11Resource* destination, ID3D11Resource* source, UINT screenWidth,
                             UINT screenHeight);

    bool IsSrvRegistered(ID3D11ShaderResourceView* srv) const;

    uint64_t UpdatedHashCount() const { return _updatedHashes; }

    void MarkReplacement(ID3D11ShaderResourceView* srv, bool replacement);

    RegistryStats GetStats() const;
    void Clear();

    struct ObservedTexture
    {
        uint32_t hash = 0;
        uint32_t format = 0;
        uint32_t width = 0;
        uint32_t height = 0;
    };
    std::vector<ObservedTexture> SnapshotObserved(std::size_t max) const;
    std::size_t ObservedCount() const;

private:
    TextureRegistry() = default;

    struct TextureEntry
    {
        uint32_t dataHash = 0;
        uint32_t hash = 0;
        bool valid = false;
        D3D11_TEXTURE2D_DESC desc{};
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
