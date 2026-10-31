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

// 采样时是否做 sRGB 解码。跨压缩族时这是必须对齐的语义之一：
// 例如游戏用 BC3_UNORM 采样的槽位，换成 BC7_UNORM 的 mod 贴图没问题，
// 但换成 BC7_UNORM_SRGB 就会整体偏暗。
bool IsSrgbFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_BC1_UNORM_SRGB:
    case DXGI_FORMAT_BC2_UNORM_SRGB:
    case DXGI_FORMAT_BC3_UNORM_SRGB:
    case DXGI_FORMAT_BC7_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return true;
    default:
        return false;
    }
}

// 8 位 UNORM 色彩贴图（含 sRGB 变体）。跨族替换只在这个集合内成立：
// 着色器拿到的都是解码后的 0..1 RGBA，压缩族只是存储方式；
// SNORM（有符号）、BC6H（HDR 浮点）等数值语义不同，不能混用。
bool IsColorUnormFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_BC1_UNORM:
    case DXGI_FORMAT_BC1_UNORM_SRGB:
    case DXGI_FORMAT_BC2_UNORM:
    case DXGI_FORMAT_BC2_UNORM_SRGB:
    case DXGI_FORMAT_BC3_UNORM:
    case DXGI_FORMAT_BC3_UNORM_SRGB:
    case DXGI_FORMAT_BC7_UNORM:
    case DXGI_FORMAT_BC7_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return true;
    default:
        return false;
    }
}

}  // namespace

TextureFormatMatch ClassifyTextureFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat)
{
    // 没有视图格式信息时保持 DDS 原样，不算不兼容。
    if (viewFormat == DXGI_FORMAT_UNKNOWN || ddsFormat == DXGI_FORMAT_UNKNOWN)
        return TextureFormatMatch::Same;

    if (ddsFormat == viewFormat)
        return TextureFormatMatch::Same;

    if (SharesDataLayout(ddsFormat, viewFormat))
        return TextureFormatMatch::Compatible;

    // 压缩族不同：只要都是 8 位 UNORM 色彩贴图且 sRGB 语义一致，就能直接按
    // DDS 自身格式建纹理（MT 就是这种情况：游戏 BC3_UNORM，mod 是 BC7_UNORM）。
    if (IsColorUnormFormat(ddsFormat) && IsColorUnormFormat(viewFormat)
        && IsSrgbFormat(ddsFormat) == IsSrgbFormat(viewFormat))
        return TextureFormatMatch::Compatible;

    return TextureFormatMatch::Unsupported;
}

DXGI_FORMAT ChooseReplacementFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat)
{
    if (viewFormat == DXGI_FORMAT_UNKNOWN || ddsFormat == DXGI_FORMAT_UNKNOWN)
        return ddsFormat;

    if (ClassifyTextureFormat(ddsFormat, viewFormat) != TextureFormatMatch::Compatible)
        return ddsFormat;

    // 同族才用视图格式（能精确复刻 sRGB 语义）；跨族只能用 DDS 自己的格式，
    // 但上面已经保证 sRGB 语义一致。
    return SharesDataLayout(ddsFormat, viewFormat) ? viewFormat : ddsFormat;
}

}  // namespace jasm::uhd
