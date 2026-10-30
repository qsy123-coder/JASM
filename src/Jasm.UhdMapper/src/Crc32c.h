#pragma once

#include <cstddef>
#include <cstdint>

namespace jasm::uhd {

// CRC32C（Castagnoli），与 3DMigoto 的贴图 hash 算法一致。
// 参数：反射多项式 0x82F63B78，init 0xFFFFFFFF，refin/refout，final xor 0xFFFFFFFF。
// 测试向量：CRC32C("123456789") == 0xE3069283。

// 分块更新：crc 是「未取反」的运行值（首块传 0xFFFFFFFF，全部喂完后再取反）。
uint32_t Crc32cUpdate(uint32_t crc, const void* data, std::size_t size);

// 一次性计算（内部完成 init 与 final xor）。
uint32_t Crc32c(const void* data, std::size_t size);

// 本机是否有 SSE4.2 快路径（日志/测试用）。
bool Crc32cHasHardwareSupport();

}  // namespace jasm::uhd
