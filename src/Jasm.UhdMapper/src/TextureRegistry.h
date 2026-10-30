#pragma once

#include <cstdint>
#include <shared_mutex>
#include <unordered_map>

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

    mutable std::shared_mutex _mutex;
    std::unordered_map<ID3D11Texture2D*, TextureEntry> _textures;
    std::unordered_map<ID3D11ShaderResourceView*, SrvEntry> _srvs;
    std::size_t _hashed = 0;
    std::size_t _unknown = 0;
};

}  // namespace jasm::uhd
