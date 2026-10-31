#pragma once

#include <dxgiformat.h>

namespace jasm::uhd {

// 游戏把角色贴图建成 BC7_TYPELESS，再用 BC7_UNORM / BC7_UNORM_SRGB 等**视图**采样；
// mod 的 DDS 头里只可能写具体格式，两者不一定一致。3DMigoto 的做法是按原始视图的格式
// 创建替换纹理 —— 压缩块的数据位相同，只是采样时是否做 sRGB 解码不同。
// 方向搞反会让整张贴图发白（少做一次 sRGB 解码）或发黑（多做一次）。
enum class TextureFormatMatch
{
    Same,        // 完全相同（或视图格式未知，不需要换算）
    Compatible,  // 同族变体：用视图格式重建即可，数据位不变
    Unsupported, // 不共享数据位（BC7 vs BC1、UNORM vs SNORM 等），需要转码，当前放弃替换
};

// ddsFormat = DDS 头里的格式；viewFormat = 原始 SRV 的格式（DXGI_FORMAT_UNKNOWN 表示未知）。
TextureFormatMatch ClassifyTextureFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat);

// 创建替换纹理时应使用的格式：
//   · 视图格式未知 → DDS 自身格式；
//   · 同族 → 视图格式（与游戏原始 SRV 的色彩空间一致）；
//   · 不兼容 → DDS 格式（调用方应先看 Classify 是否 Unsupported 并放弃替换）。
DXGI_FORMAT ChooseReplacementFormat(DXGI_FORMAT ddsFormat, DXGI_FORMAT viewFormat);

}  // namespace jasm::uhd
