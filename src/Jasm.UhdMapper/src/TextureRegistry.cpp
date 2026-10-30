#include "TextureRegistry.h"

#include <mutex>

#include <wrl/client.h>

#include "Log.h"
#include "TextureHash.h"

namespace jasm::uhd {
namespace {

using Microsoft::WRL::ComPtr;

}  // namespace

TextureRegistry& TextureRegistry::Instance()
{
    static TextureRegistry instance;
    return instance;
}

void TextureRegistry::OnTextureCreated(ID3D11Texture2D* texture, const D3D11_TEXTURE2D_DESC& desc,
                                       const D3D11_SUBRESOURCE_DATA* initialData, UINT screenWidth,
                                       UINT screenHeight)
{
    if (texture == nullptr)
        return;

    TextureEntry entry;
    entry.desc = desc;

    uint32_t dataHash = 0;
    if (ComputeTextureDataHash(desc, initialData, &dataHash))
    {
        entry.dataHash = dataHash;
        entry.hash = CombineDescHash(dataHash, desc, screenWidth, screenHeight);
        entry.valid = true;
    }

    std::unique_lock<std::shared_mutex> lock(_mutex);
    if (_textures.size() >= kMaxEntries)
    {
        // 上限兜底：清空元数据（不持有 COM 引用，安全）。之后的新纹理照常记录。
        _textures.clear();
        _srvs.clear();
        _hashed = 0;
        _unknown = 0;
        lock.unlock();
        LogWrite(LogLevel::Warn, "registry cleared (entry cap reached)");
        lock.lock();
    }

    _textures[texture] = entry;
    if (entry.valid)
    {
        ++_hashed;
        RecordObservedLocked(entry.hash, entry);
    }
    else
    {
        ++_unknown;
    }
}

void TextureRegistry::RecordObservedLocked(uint32_t hash, const TextureEntry& entry)
{
    if (_observed.size() >= kMaxObserved || !_observedSet.insert(hash).second)
        return;

    ObservedTexture item;
    item.hash = hash;
    item.format = static_cast<uint32_t>(entry.desc.Format);
    item.width = entry.desc.Width;
    item.height = entry.desc.Height;
    _observed.push_back(item);
}

void TextureRegistry::RememberSrv(ID3D11ShaderResourceView* srv, ID3D11Resource* resource)
{
    if (srv == nullptr)
        return;

    SrvEntry entry;
    entry.resource = resource;

    // QueryInterface 放在锁外，避免在锁里做 COM 调用。
    ComPtr<ID3D11Texture2D> texture;
    const bool isTexture =
        resource != nullptr && SUCCEEDED(resource->QueryInterface(IID_PPV_ARGS(texture.GetAddressOf())));

    std::unique_lock<std::shared_mutex> lock(_mutex);

    if (_srvs.size() >= kMaxEntries)
    {
        _srvs.clear();
        lock.unlock();
        LogWrite(LogLevel::Warn, "srv registry cleared (entry cap reached)");
        lock.lock();
    }

    if (isTexture)
    {
        const auto it = _textures.find(texture.Get());
        if (it != _textures.end())
        {
            entry.hash = it->second.hash;
            entry.valid = it->second.valid;
        }
    }

    _srvs[srv] = entry;
}

bool TextureRegistry::TryGetSrvHash(ID3D11ShaderResourceView* srv, uint32_t* hash,
                                    bool* isReplacement) const
{
    if (srv == nullptr)
        return false;

    std::shared_lock<std::shared_mutex> lock(_mutex);
    const auto it = _srvs.find(srv);
    if (it == _srvs.end())
        return false;

    if (isReplacement != nullptr)
        *isReplacement = it->second.replacement;

    if (!it->second.valid)
        return false;

    if (hash != nullptr)
        *hash = it->second.hash;
    return true;
}

bool TextureRegistry::TryGetTextureHash(ID3D11Resource* resource, uint32_t* hash) const
{
    if (resource == nullptr)
        return false;

    ComPtr<ID3D11Texture2D> texture;
    if (FAILED(resource->QueryInterface(IID_PPV_ARGS(texture.GetAddressOf()))))
        return false;

    std::shared_lock<std::shared_mutex> lock(_mutex);
    const auto it = _textures.find(texture.Get());
    if (it == _textures.end() || !it->second.valid)
        return false;

    if (hash != nullptr)
        *hash = it->second.hash;
    return true;
}

bool TextureRegistry::TryGetTextureDesc(ID3D11Resource* resource, D3D11_TEXTURE2D_DESC* desc) const
{
    if (resource == nullptr || desc == nullptr)
        return false;

    ComPtr<ID3D11Texture2D> texture;
    if (FAILED(resource->QueryInterface(IID_PPV_ARGS(texture.GetAddressOf()))))
        return false;

    std::shared_lock<std::shared_mutex> lock(_mutex);
    const auto it = _textures.find(texture.Get());
    if (it == _textures.end())
        return false;

    *desc = it->second.desc;
    return true;
}

void TextureRegistry::SetTextureDataHash(ID3D11Resource* resource, uint32_t dataHash,
                                         const D3D11_TEXTURE2D_DESC* desc, UINT screenWidth,
                                         UINT screenHeight)
{
    if (resource == nullptr)
        return;

    ComPtr<ID3D11Texture2D> texture;
    if (FAILED(resource->QueryInterface(IID_PPV_ARGS(texture.GetAddressOf()))))
        return;

    std::unique_lock<std::shared_mutex> lock(_mutex);

    auto it = _textures.find(texture.Get());
    if (it == _textures.end())
    {
        if (desc == nullptr || _textures.size() >= kMaxEntries)
            return;

        TextureEntry entry;
        entry.desc = *desc;
        entry.dataHash = dataHash;
        entry.hash = CombineDescHash(dataHash, entry.desc, screenWidth, screenHeight);
        entry.valid = true;
        _textures[texture.Get()] = entry;
        ++_hashed;
        ++_updatedHashes;
        RecordObservedLocked(entry.hash, entry);
        return;
    }

    // 用创建时的原始描述计算最终 hash；调用方给的实时描述不覆盖它。
    it->second.dataHash = dataHash;
    it->second.hash = CombineDescHash(dataHash, it->second.desc, screenWidth, screenHeight);
    if (!it->second.valid)
    {
        --_unknown;
        ++_hashed;
    }
    it->second.valid = true;
    ++_updatedHashes;
    RecordObservedLocked(it->second.hash, it->second);
}

void TextureRegistry::CopyTextureDataHash(ID3D11Resource* destination, ID3D11Resource* source,
                                          UINT screenWidth, UINT screenHeight)
{
    if (destination == nullptr || source == nullptr)
        return;

    ComPtr<ID3D11Texture2D> sourceTexture;
    if (FAILED(source->QueryInterface(IID_PPV_ARGS(sourceTexture.GetAddressOf()))))
        return;

    uint32_t dataHash = 0;
    bool sourceKnown = false;
    {
        std::shared_lock<std::shared_mutex> lock(_mutex);
        const auto it = _textures.find(sourceTexture.Get());
        if (it != _textures.end() && it->second.valid)
        {
            dataHash = it->second.dataHash;
            sourceKnown = true;
        }
    }

    if (!sourceKnown)
        return;

    ComPtr<ID3D11Texture2D> destinationTexture;
    if (FAILED(destination->QueryInterface(IID_PPV_ARGS(destinationTexture.GetAddressOf()))))
        return;

    std::unique_lock<std::shared_mutex> lock(_mutex);
    const auto it = _textures.find(destinationTexture.Get());
    if (it == _textures.end())
        return; // 没有目标描述，无法算最终 hash

    it->second.dataHash = dataHash;
    it->second.hash = CombineDescHash(dataHash, it->second.desc, screenWidth, screenHeight);
    if (!it->second.valid)
    {
        --_unknown;
        ++_hashed;
    }
    it->second.valid = true;
    RecordObservedLocked(it->second.hash, it->second);
}

bool TextureRegistry::IsSrvRegistered(ID3D11ShaderResourceView* srv) const
{
    if (srv == nullptr)
        return false;

    std::shared_lock<std::shared_mutex> lock(_mutex);
    return _srvs.find(srv) != _srvs.end();
}

void TextureRegistry::MarkReplacement(ID3D11ShaderResourceView* srv, bool replacement)
{
    if (srv == nullptr)
        return;

    std::unique_lock<std::shared_mutex> lock(_mutex);
    auto it = _srvs.find(srv);
    if (it != _srvs.end())
    {
        it->second.replacement = replacement;
    }
    else
    {
        SrvEntry entry;
        entry.replacement = replacement;
        _srvs[srv] = entry;
    }
}

RegistryStats TextureRegistry::GetStats() const
{
    std::shared_lock<std::shared_mutex> lock(_mutex);
    RegistryStats stats;
    stats.textures = _textures.size();
    stats.srvs = _srvs.size();
    stats.hashedTextures = _hashed;
    stats.unknownTextures = _unknown;
    return stats;
}

void TextureRegistry::Clear()
{
    std::unique_lock<std::shared_mutex> lock(_mutex);
    _textures.clear();
    _srvs.clear();
    _hashed = 0;
    _unknown = 0;
    _observedSet.clear();
    _observed.clear();
}

std::vector<TextureRegistry::ObservedTexture> TextureRegistry::SnapshotObserved(
    std::size_t max) const
{
    std::shared_lock<std::shared_mutex> lock(_mutex);
    if (max == 0 || max > _observed.size())
        return _observed;
    return std::vector<ObservedTexture>(_observed.begin(), _observed.begin() + max);
}

std::size_t TextureRegistry::ObservedCount() const
{
    std::shared_lock<std::shared_mutex> lock(_mutex);
    return _observed.size();
}

}  // namespace jasm::uhd
