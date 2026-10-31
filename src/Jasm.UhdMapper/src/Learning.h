#pragma once

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <Windows.h>
#include <d3d11.h>

namespace jasm::uhd {

// 观察学习（M3）：记录「draw 身份 + PS 槽位 → 贴图 hash」。
//
// 为什么要 draw 身份：HD 与 UHD 两档位的贴图 hash 完全不同（内容哈希），
// 只能靠「同一次 draw 的同一个槽位」把两者配对。draw 身份用
//   (索引缓冲内容 CRC32C, 该 draw 的第一个 index 值)
// 表示 —— 两个档位渲染同一个角色时这两个值稳定不变（只有贴图换了）。
//
// 每档位运行各写一份 learn.tsv，托管侧按 (ibHash, firstIndex, slot) 做内连接，
// 得到 HD hash → UHD hash 的配对，再喂给现有 UHD 覆盖段生成器。
class Learning
{
public:
    static Learning& Instance();

    // learn 模式才开始记录；普通模式所有入口都直接返回，避免给每帧加开销。
    void SetEnabled(bool enabled);
    bool Enabled() const;

    void SetOutputPath(const std::wstring& path);

    // ── 缓冲区影子（设备/上下文钩子调用）───────────────────────────────
    void OnBufferCreated(ID3D11Buffer* buffer, const D3D11_BUFFER_DESC& desc,
                         const D3D11_SUBRESOURCE_DATA* initialData);
    void OnBufferUpdated(ID3D11Buffer* buffer, const void* data);
    void OnBufferMapped(ID3D11Buffer* buffer, void* data);
    void OnBufferUnmapped(ID3D11Buffer* buffer);

    // ── draw 身份 ────────────────────────────────────────────────────
    void OnIndexBufferBound(ID3D11Buffer* buffer, DXGI_FORMAT format, UINT offset);

    // ── PS 绑定（start+i 槽位 → 纹理 hash；hash=0 表示未知）────────────
    void OnPixelShaderResourceBound(UINT slot, uint32_t hash);

    // indexCount 只用于记录，参与身份的是 startIndexLocation 处的第一个 index 值。
    void OnDrawIndexed(UINT indexCount, UINT startIndexLocation);

    // 工作线程定期调用：有变化时原子写 learn.tsv。
    void WriteSnapshot();
    std::size_t SampleCount() const;
    void Clear();

private:
    Learning() = default;

    struct BufferShadow
    {
        std::vector<uint8_t> data;
        std::size_t byteWidth = 0;
        bool known = false;
        uint32_t hash = 0;
        bool hashValid = false;
    };

    struct SampleKey
    {
        uint32_t ibHash = 0;
        uint32_t firstIndex = 0;
        uint16_t slot = 0;

        bool operator==(const SampleKey& other) const
        {
            return ibHash == other.ibHash && firstIndex == other.firstIndex && slot == other.slot;
        }
    };

    struct SampleKeyHash
    {
        std::size_t operator()(const SampleKey& key) const
        {
            std::size_t value = key.ibHash;
            value = value * 1315423911u + key.firstIndex;
            value = value * 1315423911u + key.slot;
            return value;
        }
    };

    struct Sample
    {
        uint32_t textureHash = 0;
        uint32_t count = 0;
    };

    bool EnsureShadowLocked(ID3D11Buffer* buffer, BufferShadow** shadow);
    static uint32_t ComputeBufferHash(const std::vector<uint8_t>& data);
    void WriteSnapshotLocked(const std::wstring& path);

    mutable std::mutex _mutex;
    bool _enabled = false;
    bool _dirty = false;
    std::wstring _outputPath;

    std::unordered_map<ID3D11Buffer*, BufferShadow> _buffers;
    std::unordered_map<ID3D11Buffer*, void*> _mappedBuffers;
    ID3D11Buffer* _currentIndexBuffer = nullptr;
    DXGI_FORMAT _indexFormat = DXGI_FORMAT_UNKNOWN;
    UINT _indexOffset = 0;

    uint32_t _slotHashes[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT] = {};

    std::unordered_map<SampleKey, Sample, SampleKeyHash> _samples;

    static constexpr std::size_t kMaxBuffers = 8192;
    static constexpr std::size_t kMaxBufferBytes = 32u * 1024u * 1024u;
    static constexpr std::size_t kMaxSamples = 500000;
};

}  // namespace jasm::uhd
