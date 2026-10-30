#include "Crc32c.h"

#include <array>
#include <cstring>
#include <mutex>

#include <intrin.h>
#include <nmmintrin.h>

namespace jasm::uhd {
namespace {

constexpr uint32_t kPolynomial = 0x82F63B78u;

std::array<uint32_t, 256> BuildTable()
{
    std::array<uint32_t, 256> table{};
    for (uint32_t i = 0; i < 256; ++i)
    {
        uint32_t crc = i;
        for (int bit = 0; bit < 8; ++bit)
            crc = (crc & 1u) ? ((crc >> 1) ^ kPolynomial) : (crc >> 1);
        table[i] = crc;
    }
    return table;
}

const std::array<uint32_t, 256>& Table()
{
    static const std::array<uint32_t, 256> table = BuildTable();
    return table;
}

bool DetectSse42()
{
    int registers[4] = {};
    __cpuid(registers, 1);
    return (registers[2] & (1 << 20)) != 0;
}

uint32_t UpdateSoftware(uint32_t crc, const uint8_t* data, std::size_t size)
{
    const auto& table = Table();
    for (std::size_t i = 0; i < size; ++i)
        crc = table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
    return crc;
}

uint32_t UpdateHardware(uint32_t crc, const uint8_t* data, std::size_t size)
{
    uint64_t value = crc;

    while (size >= 8)
    {
        uint64_t chunk = 0;
        memcpy(&chunk, data, sizeof(chunk));
        value = _mm_crc32_u64(value, chunk);
        data += 8;
        size -= 8;
    }

    while (size-- > 0)
        value = _mm_crc32_u8(static_cast<uint32_t>(value), *data++);

    return static_cast<uint32_t>(value);
}

}  // namespace

bool Crc32cHasHardwareSupport()
{
    static const bool supported = DetectSse42();
    return supported;
}

uint32_t Crc32cUpdate(uint32_t crc, const void* data, std::size_t size)
{
    if (data == nullptr || size == 0)
        return crc;

    const auto* bytes = static_cast<const uint8_t*>(data);
    static const bool hardware = DetectSse42();
    return hardware ? UpdateHardware(crc, bytes, size) : UpdateSoftware(crc, bytes, size);
}

uint32_t Crc32c(const void* data, std::size_t size)
{
    return ~Crc32cUpdate(0xFFFFFFFFu, data, size);
}

}  // namespace jasm::uhd
