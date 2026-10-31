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

uint32_t ReadIndexValue(const uint8_t* data, DXGI_FORMAT format)
{
    if (format == DXGI_FORMAT_R16_UINT)
    {
        uint16_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return value;
    }

    uint32_t value = 0;
    std::memcpy(&value, data, sizeof(value));
    return value;
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
    if (buffer == nullptr || (desc.BindFlags & D3D11_BIND_INDEX_BUFFER) == 0)
        return;

    std::lock_guard<std::mutex> lock(_mutex);
    if (!_enabled || desc.ByteWidth == 0 || desc.ByteWidth > kMaxBufferBytes)
        return;

    if (_buffers.size() >= kMaxBuffers)
    {
        _buffers.clear();
        _mappedBuffers.clear();
        LogWrite(LogLevel::Warn, "learning: buffer shadow cap reached; cleared");
    }

    BufferShadow shadow;
    shadow.byteWidth = desc.ByteWidth;
    if (initialData != nullptr && initialData->pSysMem != nullptr)
    {
        const auto* bytes = static_cast<const uint8_t*>(initialData->pSysMem);
        shadow.data.assign(bytes, bytes + desc.ByteWidth);
        shadow.known = true;
    }

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

    const auto* bytes = static_cast<const uint8_t*>(data);
    shadow->data.assign(bytes, bytes + shadow->byteWidth);
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
        const auto* bytes = static_cast<const uint8_t*>(mapped->second);
        shadow->data.assign(bytes, bytes + shadow->byteWidth);
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

    const std::size_t stride =
        _indexFormat == DXGI_FORMAT_R16_UINT ? sizeof(uint16_t) : sizeof(uint32_t);
    if (_indexFormat != DXGI_FORMAT_R16_UINT && _indexFormat != DXGI_FORMAT_R32_UINT)
        return;

    const std::size_t offset =
        static_cast<std::size_t>(_indexOffset) + static_cast<std::size_t>(startIndexLocation) * stride;
    if (offset + stride > shadow->data.size())
        return;

    const uint32_t firstIndex = ReadIndexValue(shadow->data.data() + offset, _indexFormat);

    if (!shadow->hashValid)
    {
        shadow->hash = ComputeBufferHash(shadow->data);
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
