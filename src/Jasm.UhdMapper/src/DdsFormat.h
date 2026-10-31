#pragma once

#include <dxgiformat.h>

namespace jasm::uhd {

// 游戏把角色贴图建成 TYPELESS，再用具体格式的**视图**采样；mod 的 DDS 头里写的是
// 另一个具体格式，两者不一定一致。像素着色器采样拿到的是解码后的 RGBA，所以：
//   · 同族（BC7_UNORM ↔ BC7_UNORM_SRGB）数据位相同，只有 sRGB 解码差异；
//   · 跨族但都是 8 位 UNORM 色彩贴图（BC3_UNORM ↔ BC7_UNORM）只是存储方式不同，
//     解码结果语义一致，可以直接用 mod 自己的格式创建纹理；
//   · sRGB 语义必须一致，方向错了整张贴图会发白（少一次 sRGB 解码）或发黑（多做一次）；
//   · SNORM（有符号）、BC6H（HDR 浮点）与 UNORM 数值语义不同，不在此列。
enum class TextureFormatMatch
{
    Same,        // 完全相同（或视图格式未知，不需要换算）
    Compatible,  // 同族变体，或跨族但同为 8 位 UNORM 且 sRGB 语义一致
    Unsupported, // 数值语义不同（UNORM vs SNORM、BC6H UF16 vs SF16、sRGB 不一致等），放弃替换
};

// ddsFormat = DDS 头里的格式；viewFormat = 原始 SRV 的格式（DXGI_FORMAT_UNKNOWN 表示未知）。
TextureFormatMatch ClassifyTextureFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat);

// 创建替换纹理时应使用的格式：
//   · 视图格式未知 → DDS 自身格式；
//   · 同族 → 视图格式（精确复刻游戏的 sRGB 语义）；
//   · 跨族但兼容 → DDS 自身格式（布局不同，只能用 DDS 的格式）；
//   · 不兼容 → DDS 格式（调用方应先看 Classify 是否 Unsupported 并放弃替换）。
DXGI_FORMAT ChooseReplacementFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat);

}  // namespace jasm::uhd
