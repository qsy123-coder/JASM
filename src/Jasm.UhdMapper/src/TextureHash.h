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

// 计算纹理 mip0 的 CRC32C hash（行 pitch 感知，padding 不参与）。
// 只在「2D、array=1、非 MSAA、有 mip0 初始数据」时返回 true。
bool ComputeTextureHash(const D3D11_TEXTURE2D_DESC& desc, const D3D11_SUBRESOURCE_DATA* initialData,
                        uint32_t* hash);

}  // namespace jasm::uhd
