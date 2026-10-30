#pragma once

#include <cstdint>
#include <string>

#include <Windows.h>
#include <d3d11.h>

namespace jasm::uhd {

// 3DMigoto 贴图 hash 的 8 位小写十六进制表示。
std::string HashToHex(uint32_t hash);

// 解析 8 位十六进制；非法或全 0 返回 false（与 RoleHashAliasLibrary.NormalizeHash 一致）。
bool ParseHashHex(const std::string& text, uint32_t* hash);

// 按 DXGI 格式计算 mip0 的单行字节数与行数。未知格式返回 false。
bool GetRowLayout(DXGI_FORMAT format, UINT width, UINT height, UINT* rowBytes, UINT* rowCount);

// mip0 数据 hash：逐行 CRC32C，跳过行 padding（3DMigoto 的 data_hash）。
bool ComputeTextureDataHash(const D3D11_TEXTURE2D_DESC& desc, const D3D11_SUBRESOURCE_DATA* initialData,
                            uint32_t* dataHash);

// 3DMigoto 用于匹配的最终 hash：CRC32C(desc 字节, seed = dataHash)。
// 宽高等于屏幕分辨率（或其 2/4/8 倍、1/2）时替换成特殊标记 —— 与 3DMigoto 的
// AdjustForConstResolution 完全一致。screenWidth/Height 为 0 时不做替换。
uint32_t CombineDescHash(uint32_t dataHash, const D3D11_TEXTURE2D_DESC& desc, UINT screenWidth,
                         UINT screenHeight);

// 一步到位（数据 hash + desc hash）。
bool ComputeTextureHash(const D3D11_TEXTURE2D_DESC& desc, const D3D11_SUBRESOURCE_DATA* initialData,
                        UINT screenWidth, UINT screenHeight, uint32_t* hash);

}  // namespace jasm::uhd
