#pragma once

#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

#include <Windows.h>
#include <d3d11.h>
#include <wrl/client.h>

#include "AliasTable.h"

namespace jasm::uhd {

// manifest 里的 ddsPath → ID3D11Texture2D/SRV 的懒加载缓存。
// 只由 PSSetShaderResources 的替换路径调用；失败有冷却，避免反复读坏文件。
class DdsCache
{
public:
    static DdsCache& Instance();

    // 命中缓存或成功加载时返回 true，并给出一个已 AddRef 的 SRV（调用方负责 Release）。
    // viewFormat = 原始 SRV 的视图格式（DXGI_FORMAT_UNKNOWN 表示未知）：替换纹理按它创建，
    // 保证 sRGB/线性语义与游戏原始贴图一致。同一张 DDS 可能被不同格式的视图绑定，
    // 缓存键因此包含格式。
    bool GetOrCreate(ID3D11Device* device, uint32_t uhdHash, const AliasTarget& target,
                     DXGI_FORMAT viewFormat, ID3D11ShaderResourceView** view);

    struct Stats
    {
        std::size_t entries = 0;
        uint64_t bytes = 0;
        uint64_t failures = 0;
    };
    Stats GetStats() const;

    void SetLimits(uint64_t maxCacheBytes, uint64_t maxTextureBytes);

private:
    DdsCache() = default;

    struct Entry
    {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view;
        uint64_t bytes = 0;
    };

    struct Failure
    {
        uint64_t failedAtMs = 0;
    };

    static std::string MakeKey(uint32_t uhdHash, DXGI_FORMAT viewFormat,
                               const std::wstring& path);
    static bool LoadTexture(ID3D11Device* device, const std::wstring& path,
                            DXGI_FORMAT viewFormat,
                            Microsoft::WRL::ComPtr<ID3D11Texture2D>* texture,
                            Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>* view, uint64_t* bytes,
                            DXGI_FORMAT* createdFormat);

    mutable std::mutex _mutex;
    std::unordered_map<std::string, Entry> _entries;
    std::unordered_map<std::string, Failure> _failed;
    uint64_t _bytes = 0;
    uint64_t _failures = 0;
    uint64_t _maxCacheBytes = 1024ull * 1024 * 1024;
    uint64_t _maxTextureBytes = 256ull * 1024 * 1024;
};

}  // namespace jasm::uhd
