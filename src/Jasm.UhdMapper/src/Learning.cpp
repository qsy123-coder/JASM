#include "Learning.h"

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>

#include "Crc32c.h"
#include "Log.h"
#include "Manifest.h"

namespace jasm::uhd {
namespace {

// 从游戏内存拷数据：源指针可能已经失效（缓冲区/映射指针会被复用），
// 用 SEH 兜底 —— 读坏了只丢这次采样，绝不能把游戏打崩。
bool SafeAssign(std::vector<uint8_t>& target, const void* source, std::size_t count)
{
    if (source == nullptr || count == 0)
        return false;

    __try
    {
        const auto* bytes = static_cast<const uint8_t*>(source);
        target.assign(bytes, bytes + count);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        target.clear();
        return false;
    }
}

}  // namespace

Learning& Learning::Instance()
{
    static Learning instance;
    return instance;
}

void Learning::SetEnabled(bool enabled)
{
    std::lock_guard<std::mutex> lock(_mutex);
    _enabled = enabled;
    if (!enabled)
    {
        _buffers.clear();
        _mappedBuffers.clear();
        _samples.clear();
        _currentIndexBuffer = nullptr;
        _dirty = false;
    }
}

bool Learning::Enabled() const
{
    std::lock_guard<std::mutex> lock(_mutex);
    return _enabled;
}

void Learning::SetOutputPath(const std::wstring& path)
{
    std::lock_guard<std::mutex> lock(_mutex);
    _outputPath = path;
}

bool Learning::EnsureShadowLocked(ID3D11Buffer* buffer, BufferShadow** shadow)
{
    const auto it = _buffers.find(buffer);
    if (it == _buffers.end())
        return false;

    *shadow = &it->second;
    return true;
}

uint32_t Learning::ComputeBufferHash(const std::vector<uint8_t>& data)
{
    return data.empty() ? 0 : Crc32c(data.data(), data.size());
}

void Learning::OnBufferCreated(ID3D11Buffer* buffer, const D3D11_BUFFER_DESC& desc,
                               const D3D11_SUBRESOURCE_DATA* initialData)
{
    if (buffer == nullptr)
        return;

    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled)
        return;

    // 指针复用：同一个地址上可能是新的资源。旧影子/旧映射指针必须先清掉，
    // 否则会拿旧缓冲的尺寸去拷贝新映射（实测会读越界崩 UE）。
    _buffers.erase(buffer);
    _mappedBuffers.erase(buffer);

    if ((desc.BindFlags & D3D11_BIND_INDEX_BUFFER) == 0 || desc.ByteWidth == 0
        || desc.ByteWidth > kMaxBufferBytes)
        return;

    if (_buffers.size() >= kMaxBuffers)
    {
        _buffers.clear();
        _mappedBuffers.clear();
        LogWrite(LogLevel::Warn, "learning: buffer shadow cap reached; cleared");
    }

    BufferShadow shadow;
    shadow.byteWidth = desc.ByteWidth;
    shadow.desc = desc;
    shadow.descValid = true;
    if (initialData != nullptr && initialData->pSysMem != nullptr)
        shadow.known = SafeAssign(shadow.data, initialData->pSysMem, desc.ByteWidth);

    _buffers[buffer] = std::move(shadow);
}

void Learning::OnBufferUpdated(ID3D11Buffer* buffer, const void* data)
{
    if (buffer == nullptr || data == nullptr)
        return;

    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled)
        return;

    BufferShadow* shadow = nullptr;
    if (!EnsureShadowLocked(buffer, &shadow) || shadow->byteWidth == 0)
        return;

    if (SafeAssign(shadow->data, data, shadow->byteWidth))
        shadow->known = true;
    shadow->hashValid = false;
    _dirty = true;
}

void Learning::OnBufferMapped(ID3D11Buffer* buffer, void* data)
{
    if (buffer == nullptr || data == nullptr)
        return;

    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled)
        return;

    BufferShadow* shadow = nullptr;
    if (!EnsureShadowLocked(buffer, &shadow))
        return;

    _mappedBuffers[buffer] = data;
}

void Learning::OnBufferUnmapped(ID3D11Buffer* buffer)
{
    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled || buffer == nullptr)
        return;

    const auto mapped = _mappedBuffers.find(buffer);
    if (mapped == _mappedBuffers.end())
        return;

    BufferShadow* shadow = nullptr;
    if (EnsureShadowLocked(buffer, &shadow) && shadow->byteWidth != 0)
    {
        if (SafeAssign(shadow->data, mapped->second, shadow->byteWidth))
            shadow->known = true;
        shadow->hashValid = false;
        _dirty = true;
    }

    _mappedBuffers.erase(mapped);
}

void Learning::OnIndexBufferBound(ID3D11Buffer* buffer, DXGI_FORMAT format, UINT offset)
{
    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled)
        return;

    _currentIndexBuffer = buffer;
    _indexFormat = format;
    _indexOffset = offset;
}

void Learning::OnPixelShaderResourceBound(UINT slot, uint32_t hash)
{
    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled || slot >= D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT)
        return;

    _slotHashes[slot] = hash;
}

void Learning::OnDrawIndexed(UINT indexCount, UINT startIndexLocation)
{
    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled || _currentIndexBuffer == nullptr)
        return;

    BufferShadow* shadow = nullptr;
    if (!EnsureShadowLocked(_currentIndexBuffer, &shadow) || !shadow->known)
        return;

    if (_indexFormat != DXGI_FORMAT_R16_UINT && _indexFormat != DXGI_FORMAT_R32_UINT)
        return;

    // 身份位置 = draw 的 StartIndexLocation（Rocket 的 match_first_index 就是它；
    // 组件计数前缀和 0→17970→63606 可证）。不要再读「第一个 index 值」。
    const uint32_t firstIndex = startIndexLocation;

    if (!shadow->hashValid)
    {
        shadow->hash = ComputeBufferHash(shadow->data);

        // 诊断：3DMigoto 的 IB hash 是 mod.ini 里的 hash =，算法未知；
        // 一次真机跑日志即可判断是「纯内容」还是「内容+描述」的组合。
        if (shadow->descValid)
        {
            const uint32_t dataHash = shadow->hash;
            const auto* descBytes = reinterpret_cast<const uint8_t*>(&shadow->desc);
            const uint32_t descSeed = Crc32c(descBytes, sizeof(shadow->desc));
            const uint32_t combined1 = Crc32cUpdate(dataHash ^ 0xFFFFFFFFu, descBytes,
                                                    sizeof(shadow->desc)) ^ 0xFFFFFFFFu;
            const uint32_t combined2 = Crc32cUpdate(descSeed ^ 0xFFFFFFFFu, shadow->data.data(),
                                                    shadow->data.size()) ^ 0xFFFFFFFFu;
            LogWrite(LogLevel::Info,
                     "ibhash data=%08x c1=%08x c2=%08x width=%u bind=%08x usage=%u cpu=%u misc=%08x stride=%u",
                     dataHash, combined1, combined2, shadow->desc.ByteWidth,
                     shadow->desc.BindFlags, shadow->desc.Usage,
                     shadow->desc.CPUAccessFlags, shadow->desc.MiscFlags,
                     shadow->desc.StructureByteStride);
        }

        shadow->hashValid = true;
    }

    if (_samples.size() >= kMaxSamples)
        return;

    for (UINT slot = 0; slot < D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT; ++slot)
    {
        const uint32_t textureHash = _slotHashes[slot];
        if (textureHash == 0)
            continue;

        const SampleKey key{shadow->hash, firstIndex, static_cast<uint16_t>(slot)};
        auto& sample = _samples[key];
        if (sample.textureHash != 0 && sample.textureHash != textureHash)
        {
            // 同一位置出现了不同贴图（切换 mod/状态）：保留先到的，避免把配对搅乱。
            continue;
        }

        sample.textureHash = textureHash;
        if (sample.count < 0xFFFFFFFFu)
            ++sample.count;
        _dirty = true;
    }

    (void)indexCount;
}

void Learning::WriteSnapshot()
{
    std::lock_guard<std::mutex> lock(_mutex);
    if (!_dirty || _outputPath.empty())
        return;

    WriteSnapshotLocked(_outputPath);
    _dirty = false;
}

void Learning::WriteSnapshotLocked(const std::wstring& path)
{
    const std::wstring temp = path + L".tmp";
    std::ofstream file(std::filesystem::path(temp), std::ios::out | std::ios::binary | std::ios::trunc);
    if (!file.is_open())
        return;

    file << "# JASM learn v1\n";
    file << "# ib_hash\tfirst_index\tslot\ttexture_hash\tcount\n";

    char line[128] = {};
    for (const auto& pair : _samples)
    {
        const SampleKey& key = pair.first;
        const Sample& sample = pair.second;
        std::snprintf(line, sizeof(line), "%08x\t%u\t%u\t%08x\t%u\n", key.ibHash, key.firstIndex,
                      static_cast<unsigned>(key.slot), sample.textureHash,
                      static_cast<unsigned>(sample.count));
        file << line;
    }

    file.close();

    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        LogWrite(LogLevel::Warn, "learning: MoveFileEx failed path=%s error=%lu",
                 WideToUtf8(path).c_str(), GetLastError());
    }
}

std::size_t Learning::SampleCount() const
{
    std::lock_guard<std::mutex> lock(_mutex);
    return _samples.size();
}

void Learning::Clear()
{
    std::lock_guard<std::mutex> lock(_mutex);
    _samples.clear();
    _dirty = false;
}

}  // namespace jasm::uhd
