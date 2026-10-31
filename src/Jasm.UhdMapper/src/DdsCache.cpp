#include "DdsCache.h"

#include <cstdio>
#include <vector>

#include <DirectXTex.h>

#include "DdsFormat.h"
#include "Log.h"
#include "Manifest.h"
#include "PathSafety.h"

namespace jasm::uhd {
namespace {

constexpr uint64_t kMaxDdsFileBytes = 512ull * 1024 * 1024;
constexpr uint64_t kFailureCooldownMs = 60ull * 1000;

uint64_t NowMs()
{
    return GetTickCount64();
}

}  // namespace

DdsCache& DdsCache::Instance()
{
    static DdsCache instance;
    return instance;
}

void DdsCache::SetLimits(uint64_t maxCacheBytes, uint64_t maxTextureBytes)
{
    std::lock_guard<std::mutex> lock(_mutex);
    if (maxCacheBytes != 0)
        _maxCacheBytes = maxCacheBytes;
    if (maxTextureBytes != 0)
        _maxTextureBytes = maxTextureBytes;
}

std::string DdsCache::MakeKey(uint32_t uhdHash, DXGI_FORMAT viewFormat, const std::wstring& path)
{
    // 格式进键：同一张 DDS 可能分别以线性/SRGB 视图绑定到不同槽位，
    // 两者必须各自持有一份按对应格式创建的纹理。
    char prefix[32] = {};
    std::snprintf(prefix, sizeof(prefix), "%08x|%d|", uhdHash, static_cast<int>(viewFormat));
    return std::string(prefix) + WideToUtf8(path);
}

bool DdsCache::LoadTexture(ID3D11Device* device, const std::wstring& path, DXGI_FORMAT viewFormat,
                           Microsoft::WRL::ComPtr<ID3D11Texture2D>* texture,
                           Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>* view, uint64_t* bytes,
                           DXGI_FORMAT* createdFormat)
{
    if (device == nullptr || texture == nullptr || view == nullptr || bytes == nullptr)
        return false;

    // 文件大小上限在解析前检查，避免被畸形/巨文件拖死。
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
        return false;

    const uint64_t fileBytes = (static_cast<uint64_t>(attributes.nFileSizeHigh) << 32)
                               | attributes.nFileSizeLow;
    if (fileBytes == 0 || fileBytes > kMaxDdsFileBytes)
        return false;

    DirectX::TexMetadata metadata{};
    DirectX::ScratchImage image;
    const HRESULT loadResult =
        DirectX::LoadFromDDSFile(path.c_str(), DirectX::DDS_FLAGS_NONE, &metadata, image);
    if (FAILED(loadResult))
        return false;

    if (metadata.dimension != DirectX::TEX_DIMENSION_TEXTURE2D || metadata.arraySize != 1
        || metadata.IsCubemap() || metadata.mipLevels == 0)
        return false;

    // 按原始 SRV 的格式建纹理：同一份压缩块在 UNORM / UNORM_SRGB 之间位布局相同，
    // 只是采样时是否做 sRGB 解码 —— 用错方向整张贴图会发白或发黑。
    const TextureFormatMatch formatMatch = ClassifyTextureFormat(metadata.format, viewFormat);
    if (formatMatch == TextureFormatMatch::Unsupported)
    {
        LogWrite(LogLevel::Warn,
                 "dds format unsupported dds_fmt=%d view_fmt=%d path=%s",
                 static_cast<int>(metadata.format), static_cast<int>(viewFormat),
                 WideToUtf8(path).c_str());
        return false;
    }

    const DXGI_FORMAT textureFormat = ChooseReplacementFormat(metadata.format, viewFormat);

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(metadata.width);
    desc.Height = static_cast<UINT>(metadata.height);
    desc.MipLevels = static_cast<UINT>(metadata.mipLevels);
    desc.ArraySize = 1;
    desc.Format = textureFormat;
    desc.SampleDesc.Count = 1;
    desc.SampleDesc.Quality = 0;
    desc.Usage = D3D11_USAGE_IMMUTABLE;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;

    if (image.GetImageCount() < desc.MipLevels)
        return false;

    std::vector<D3D11_SUBRESOURCE_DATA> subresources;
    subresources.reserve(desc.MipLevels);
    for (UINT i = 0; i < desc.MipLevels; ++i)
    {
        const DirectX::Image* item = image.GetImage(i, 0, 0);
        if (item == nullptr || item->pixels == nullptr)
            return false;

        D3D11_SUBRESOURCE_DATA data{};
        data.pSysMem = item->pixels;
        data.SysMemPitch = static_cast<UINT>(item->rowPitch);
        data.SysMemSlicePitch = static_cast<UINT>(item->slicePitch);
        subresources.push_back(data);
    }

    Microsoft::WRL::ComPtr<ID3D11Texture2D> created;
    if (FAILED(device->CreateTexture2D(&desc, subresources.data(), created.GetAddressOf())))
        return false;

    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> createdView;
    if (FAILED(device->CreateShaderResourceView(created.Get(), nullptr, createdView.GetAddressOf())))
        return false;

    *texture = std::move(created);
    *view = std::move(createdView);
    *bytes = image.GetPixelsSize();
    if (createdFormat != nullptr)
        *createdFormat = textureFormat;
    return true;
}

bool DdsCache::GetOrCreate(ID3D11Device* device, uint32_t uhdHash, const AliasTarget& target,
                           DXGI_FORMAT viewFormat, ID3D11ShaderResourceView** view)
{
    if (device == nullptr || view == nullptr)
        return false;

    *view = nullptr;

    // 路径安全：只允许 mod 根目录下的 .dds（manifest 是中等权限进程写的，按不可信输入处理）。
    if (target.ddsPath.empty() || !HasDdsExtension(target.ddsPath)
        || !IsUnderDirectory(target.ddsPath, target.modRoot))
    {
        return false;
    }

    const std::string key = MakeKey(uhdHash, viewFormat, target.ddsPath);

    {
        std::lock_guard<std::mutex> lock(_mutex);

        const auto existing = _entries.find(key);
        if (existing != _entries.end())
        {
            *view = existing->second.view.Get();
            (*view)->AddRef();
            return true;
        }

        const auto failed = _failed.find(key);
        if (failed != _failed.end() && NowMs() - failed->second.failedAtMs < kFailureCooldownMs)
            return false;
    }

    // 加载在锁外进行；失败/成功都要回到锁内处理竞态。
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> createdView;
    uint64_t bytes = 0;
    DXGI_FORMAT chosenFormat = viewFormat;

    {
        std::lock_guard<std::mutex> lock(_mutex);
        const auto failed = _failed.find(key);
        if (failed != _failed.end())
            _failed.erase(failed); // 冷却已过，重新尝试
    }

    if (!LoadTexture(device, target.ddsPath, viewFormat, &texture, &createdView, &bytes,
                     &chosenFormat))
    {
        std::lock_guard<std::mutex> lock(_mutex);
        _failed[key] = Failure{NowMs()};
        ++_failures;
        return false;
    }

    std::lock_guard<std::mutex> lock(_mutex);

    auto inserted = _entries.find(key);
    if (inserted != _entries.end())
    {
        // 另一个线程抢先加载：用它的，丢弃本次结果。
        *view = inserted->second.view.Get();
        (*view)->AddRef();
        return true;
    }

    if (bytes > _maxTextureBytes || _bytes + bytes > _maxCacheBytes)
    {
        // 超上限：不缓存也不替换，保持原贴图。
        _failed[key] = Failure{NowMs()};
        ++_failures;
        LogWrite(LogLevel::Warn, "dds cache limit exceeded bytes=%llu path=%s",
                 static_cast<unsigned long long>(bytes), WideToUtf8(target.ddsPath).c_str());
        return false;
    }

    Entry entry;
    entry.texture = std::move(texture);
    entry.view = std::move(createdView);
    entry.bytes = bytes;
    _bytes += bytes;

    *view = entry.view.Get();
    (*view)->AddRef();

    _entries.emplace(key, std::move(entry));

    LogWrite(LogLevel::Info, "dds loaded hash=%08x view_fmt=%d tex_fmt=%d bytes=%llu path=%s",
             uhdHash, static_cast<int>(viewFormat), static_cast<int>(chosenFormat),
             static_cast<unsigned long long>(bytes), WideToUtf8(target.ddsPath).c_str());
    return true;
}

DdsCache::Stats DdsCache::GetStats() const
{
    std::lock_guard<std::mutex> lock(_mutex);
    Stats stats;
    stats.entries = _entries.size();
    stats.bytes = _bytes;
    stats.failures = _failures;
    return stats;
}

}  // namespace jasm::uhd
