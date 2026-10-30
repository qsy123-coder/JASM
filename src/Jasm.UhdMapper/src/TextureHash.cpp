#include "TextureHash.h"

#include <cstdio>
#include <cstring>

#include "Crc32c.h"

namespace jasm::uhd {
namespace {

struct FormatLayout
{
    DXGI_FORMAT format;
    UINT blockWidth;
    UINT blockHeight;
    UINT blockBytes;
};

// 只列 3DMigoto hash 会用到的常见格式；M1 日志里 unknown 格式会带 DXGI 值，便于后续补表。
constexpr FormatLayout kFormats[] = {
    {DXGI_FORMAT_BC1_TYPELESS, 4, 4, 8},
    {DXGI_FORMAT_BC1_UNORM, 4, 4, 8},
    {DXGI_FORMAT_BC1_UNORM_SRGB, 4, 4, 8},
    {DXGI_FORMAT_BC4_TYPELESS, 4, 4, 8},
    {DXGI_FORMAT_BC4_UNORM, 4, 4, 8},
    {DXGI_FORMAT_BC4_SNORM, 4, 4, 8},
    {DXGI_FORMAT_BC2_TYPELESS, 4, 4, 16},
    {DXGI_FORMAT_BC2_UNORM, 4, 4, 16},
    {DXGI_FORMAT_BC2_UNORM_SRGB, 4, 4, 16},
    {DXGI_FORMAT_BC3_TYPELESS, 4, 4, 16},
    {DXGI_FORMAT_BC3_UNORM, 4, 4, 16},
    {DXGI_FORMAT_BC3_UNORM_SRGB, 4, 4, 16},
    {DXGI_FORMAT_BC5_TYPELESS, 4, 4, 16},
    {DXGI_FORMAT_BC5_UNORM, 4, 4, 16},
    {DXGI_FORMAT_BC5_SNORM, 4, 4, 16},
    {DXGI_FORMAT_BC6H_TYPELESS, 4, 4, 16},
    {DXGI_FORMAT_BC6H_UF16, 4, 4, 16},
    {DXGI_FORMAT_BC6H_SF16, 4, 4, 16},
    {DXGI_FORMAT_BC7_TYPELESS, 4, 4, 16},
    {DXGI_FORMAT_BC7_UNORM, 4, 4, 16},
    {DXGI_FORMAT_BC7_UNORM_SRGB, 4, 4, 16},
    {DXGI_FORMAT_R8G8B8A8_TYPELESS, 1, 1, 4},
    {DXGI_FORMAT_R8G8B8A8_UNORM, 1, 1, 4},
    {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, 1, 1, 4},
    {DXGI_FORMAT_R8G8B8A8_SNORM, 1, 1, 4},
    {DXGI_FORMAT_B8G8R8A8_UNORM, 1, 1, 4},
    {DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, 1, 1, 4},
    {DXGI_FORMAT_R10G10B10A2_UNORM, 1, 1, 4},
    {DXGI_FORMAT_R8_UNORM, 1, 1, 1},
    {DXGI_FORMAT_R8_SNORM, 1, 1, 1},
    {DXGI_FORMAT_A8_UNORM, 1, 1, 1},
    {DXGI_FORMAT_R8G8_UNORM, 1, 1, 2},
    {DXGI_FORMAT_R16_FLOAT, 1, 1, 2},
    {DXGI_FORMAT_R16_UNORM, 1, 1, 2},
    {DXGI_FORMAT_R16G16_FLOAT, 1, 1, 4},
    {DXGI_FORMAT_R16G16_UNORM, 1, 1, 4},
    {DXGI_FORMAT_R16G16B16A16_FLOAT, 1, 1, 8},
    {DXGI_FORMAT_R16G16B16A16_UNORM, 1, 1, 8},
    {DXGI_FORMAT_R32_FLOAT, 1, 1, 4},
    {DXGI_FORMAT_R32G32_FLOAT, 1, 1, 8},
    {DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 1, 16},
};

const FormatLayout* FindLayout(DXGI_FORMAT format)
{
    for (const auto& entry : kFormats)
    {
        if (entry.format == format)
            return &entry;
    }
    return nullptr;
}

}  // namespace

std::string HashToHex(uint32_t hash)
{
    char buffer[16] = {};
    std::snprintf(buffer, sizeof(buffer), "%08x", hash);
    return buffer;
}

bool ParseHashHex(const std::string& text, uint32_t* hash)
{
    if (hash == nullptr || text.size() != 8)
        return false;

    uint32_t value = 0;
    for (char c : text)
    {
        uint32_t digit = 0;
        if (c >= '0' && c <= '9')
            digit = static_cast<uint32_t>(c - '0');
        else if (c >= 'a' && c <= 'f')
            digit = static_cast<uint32_t>(c - 'a') + 10;
        else if (c >= 'A' && c <= 'F')
            digit = static_cast<uint32_t>(c - 'A') + 10;
        else
            return false;

        value = (value << 4) | digit;
    }

    if (value == 0)
        return false;

    *hash = value;
    return true;
}

bool GetRowLayout(DXGI_FORMAT format, UINT width, UINT height, UINT* rowBytes, UINT* rowCount)
{
    const FormatLayout* layout = FindLayout(format);
    if (layout == nullptr || width == 0 || height == 0)
        return false;

    const UINT blocksX = (width + layout->blockWidth - 1) / layout->blockWidth;
    const UINT blocksY = (height + layout->blockHeight - 1) / layout->blockHeight;

    if (rowBytes != nullptr)
        *rowBytes = blocksX * layout->blockBytes;
    if (rowCount != nullptr)
        *rowCount = blocksY;
    return true;
}

bool ComputeTextureHash(const D3D11_TEXTURE2D_DESC& desc, const D3D11_SUBRESOURCE_DATA* initialData,
                        uint32_t* hash)
{
    if (hash == nullptr || initialData == nullptr || initialData->pSysMem == nullptr)
        return false;

    // 只处理单 array slice、非 MSAA 的普通 2D 纹理；其余在 M1 里记为 unknown。
    if (desc.ArraySize != 1 || desc.SampleDesc.Count != 1)
        return false;

    UINT rowBytes = 0;
    UINT rowCount = 0;
    if (!GetRowLayout(desc.Format, desc.Width, desc.Height, &rowBytes, &rowCount))
        return false;

    const UINT pitch = initialData->SysMemPitch;
    if (pitch < rowBytes)
        return false;

    // 防御性上限：畸形描述/超巨纹理不参与 hash，避免一次巨大的越界读。
    constexpr unsigned long long kMaxHashBytes = 512ull * 1024 * 1024;
    if (static_cast<unsigned long long>(rowBytes) * rowCount > kMaxHashBytes)
        return false;

    const auto* bytes = static_cast<const uint8_t*>(initialData->pSysMem);
    uint32_t crc = 0xFFFFFFFFu;
    for (UINT row = 0; row < rowCount; ++row)
        crc = Crc32cUpdate(crc, bytes + static_cast<std::size_t>(row) * pitch, rowBytes);

    *hash = ~crc;
    return true;
}

}  // namespace jasm::uhd
