#include "DdsFormat.h"

namespace jasm::uhd {
namespace {

// 「数据位完全相同」的格式组：组内差异只有 typeless 标记或是否 sRGB 解码，
// 因而可以把同一份压缩块直接按目标格式创建纹理。
//
// 注意 SNORM 与 UNORM 虽然尺寸相同，但解码规则不同（有符号 vs 无符号），
// BC6H 的 UF16 / SF16 同理 —— 这些必须分开成组。
int DataLayoutGroup(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_BC1_TYPELESS:
    case DXGI_FORMAT_BC1_UNORM:
    case DXGI_FORMAT_BC1_UNORM_SRGB:
        return 1;

    case DXGI_FORMAT_BC2_TYPELESS:
    case DXGI_FORMAT_BC2_UNORM:
    case DXGI_FORMAT_BC2_UNORM_SRGB:
        return 2;

    case DXGI_FORMAT_BC3_TYPELESS:
    case DXGI_FORMAT_BC3_UNORM:
    case DXGI_FORMAT_BC3_UNORM_SRGB:
        return 3;

    case DXGI_FORMAT_BC4_TYPELESS:
    case DXGI_FORMAT_BC4_UNORM:
        return 4;
    case DXGI_FORMAT_BC4_SNORM:
        return 5;

    case DXGI_FORMAT_BC5_TYPELESS:
    case DXGI_FORMAT_BC5_UNORM:
        return 6;
    case DXGI_FORMAT_BC5_SNORM:
        return 7;

    case DXGI_FORMAT_BC6H_TYPELESS:
    case DXGI_FORMAT_BC6H_UF16:
        return 8;
    case DXGI_FORMAT_BC6H_SF16:
        return 9;

    case DXGI_FORMAT_BC7_TYPELESS:
    case DXGI_FORMAT_BC7_UNORM:
    case DXGI_FORMAT_BC7_UNORM_SRGB:
        return 10;

    // 8 位无压缩格式同理：UNORM 与 UNORM_SRGB 的字节布局一致。
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return 11;

    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return 12;

    default:
        return 0;
    }
}

bool SharesDataLayout(DXGI_FORMAT left, DXGI_FORMAT right)
{
    const int leftGroup = DataLayoutGroup(left);
    return leftGroup != 0 && leftGroup == DataLayoutGroup(right);
}

}  // namespace

TextureFormatMatch ClassifyTextureFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat)
{
    // 没有视图格式信息时保持 DDS 原样，不算不兼容。
    if (viewFormat == DXGI_FORMAT_UNKNOWN || ddsFormat == DXGI_FORMAT_UNKNOWN)
        return TextureFormatMatch::Same;

    if (ddsFormat == viewFormat)
        return TextureFormatMatch::Same;

    return SharesDataLayout(ddsFormat, viewFormat) ? TextureFormatMatch::Compatible
                                                   : TextureFormatMatch::Unsupported;
}

DXGI_FORMAT ChooseReplacementFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat)
{
    if (viewFormat == DXGI_FORMAT_UNKNOWN)
        return ddsFormat;

    return ClassifyTextureFormat(ddsFormat, viewFormat) == TextureFormatMatch::Compatible
               ? viewFormat
               : ddsFormat;
}

}  // namespace jasm::uhd
