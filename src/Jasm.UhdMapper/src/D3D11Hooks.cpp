#include "D3D11Hooks.h"

#include <atomic>
#include <mutex>
#include <string>
#include <unordered_set>
#include <vector>

#include <Windows.h>
#include <d3d11.h>
#include <wrl/client.h>

#include <MinHook.h>

#include "AliasTable.h"
#include "Log.h"
#include "Manifest.h"
#include "TextureHash.h"
#include "TextureRegistry.h"

namespace jasm::uhd {
namespace {

using Microsoft::WRL::ComPtr;

using CreateDeviceAndSwapChainFn = HRESULT(WINAPI*)(
    IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT,
    const DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**, ID3D11Device**, D3D_FEATURE_LEVEL*,
    ID3D11DeviceContext**);

using CreateDeviceFn = HRESULT(WINAPI*)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
                                        const D3D_FEATURE_LEVEL*, UINT, UINT, ID3D11Device**,
                                        D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);

using CreateTexture2DFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*,
                                                      const D3D11_SUBRESOURCE_DATA*,
                                                      ID3D11Texture2D**);

using CreateShaderResourceViewFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, ID3D11Resource*,
                                                               const D3D11_SHADER_RESOURCE_VIEW_DESC*,
                                                               ID3D11ShaderResourceView**);

using PSSetShaderResourcesFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT,
                                                        ID3D11ShaderResourceView* const*);

using MapFn = HRESULT(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, D3D11_MAP,
                                          UINT, D3D11_MAPPED_SUBRESOURCE*);
using UnmapFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT);
using CopySubresourceRegionFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT,
                                                         UINT, UINT, UINT, ID3D11Resource*, UINT,
                                                         const D3D11_BOX*);
using CopyResourceFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*,
                                                ID3D11Resource*);
using UpdateSubresourceFn = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT,
                                                     const D3D11_BOX*, const void*, UINT, UINT);

CreateDeviceAndSwapChainFn g_origCreateDeviceAndSwapChain = nullptr;
CreateDeviceFn g_origCreateDevice = nullptr;

CreateTexture2DFn g_origCreateTexture2D = nullptr;
CreateShaderResourceViewFn g_origCreateShaderResourceView = nullptr;
PSSetShaderResourcesFn g_origPSSetShaderResources = nullptr;
MapFn g_origMap = nullptr;
UnmapFn g_origUnmap = nullptr;
CopySubresourceRegionFn g_origCopySubresourceRegion = nullptr;
CopyResourceFn g_origCopyResource = nullptr;
UpdateSubresourceFn g_origUpdateSubresource = nullptr;

std::atomic<bool> g_installed{false};
std::atomic<bool> g_applyMode{false};
std::atomic<const AliasTable*> g_aliasTable{nullptr};

std::atomic<uint64_t> g_binds{0};
std::atomic<uint64_t> g_hashedBinds{0};
std::atomic<uint64_t> g_unknownBinds{0};
std::atomic<uint64_t> g_hdBinds{0};
std::atomic<uint64_t> g_uhdBinds{0};
std::atomic<uint64_t> g_failures{0};
std::atomic<uint64_t> g_substitutions{0};

constexpr std::size_t kMaxLoggedHashes = 96;

std::mutex g_logMutex;
std::unordered_set<uint32_t> g_loggedHashes;

// M1 诊断：记录少量通过更新路径算出的 hash + 格式/尺寸，便于和 Rocket 学习记录比对。
std::mutex g_textureLogMutex;
std::unordered_set<uint32_t> g_textureLogged;
constexpr std::size_t kMaxLoggedTextures = 32;

void LogTextureOnce(uint32_t hash, const D3D11_TEXTURE2D_DESC& desc)
{
    {
        std::lock_guard<std::mutex> lock(g_textureLogMutex);
        if (g_textureLogged.size() >= kMaxLoggedTextures || !g_textureLogged.insert(hash).second)
            return;
    }

    LogWrite(LogLevel::Info, "tex hash=%s fmt=%d %ux%u mips=%u src=update",
             HashToHex(hash).c_str(), static_cast<int>(desc.Format), desc.Width, desc.Height,
             desc.MipLevels);
}

// Map/Unmap 配对：UE 大量纹理是先创建空纹理、再写映射内存，创建时没有初始数据。
struct MapRecord
{
    ID3D11Resource* resource = nullptr;
    UINT subresource = 0;
    void* data = nullptr;
    UINT rowPitch = 0;
};

std::mutex g_mapMutex;
std::vector<MapRecord> g_maps;

void RememberMap(ID3D11Resource* resource, UINT subresource, void* data, UINT rowPitch)
{
    std::lock_guard<std::mutex> lock(g_mapMutex);
    for (auto& record : g_maps)
    {
        if (record.resource == resource && record.subresource == subresource)
        {
            record.data = data;
            record.rowPitch = rowPitch;
            return;
        }
    }
    g_maps.push_back(MapRecord{resource, subresource, data, rowPitch});
}

bool TakeMap(ID3D11Resource* resource, UINT subresource, MapRecord* out)
{
    std::lock_guard<std::mutex> lock(g_mapMutex);
    for (auto it = g_maps.begin(); it != g_maps.end(); ++it)
    {
        if (it->resource == resource && it->subresource == subresource)
        {
            *out = *it;
            g_maps.erase(it);
            return true;
        }
    }
    return false;
}

bool HashMappedResource(ID3D11Resource* resource, const MapRecord& record, uint32_t* hash)
{
    D3D11_TEXTURE2D_DESC desc{};
    if (!TextureRegistry::Instance().TryGetTextureDesc(resource, &desc))
        return false;

    D3D11_SUBRESOURCE_DATA data{};
    data.pSysMem = record.data;
    data.SysMemPitch = record.rowPitch;
    return ComputeTextureHash(desc, &data, hash);
}

// ── vtable 打点 ──────────────────────────────────────────────────────────

bool PatchVtableSlot(void** vtable, std::size_t index, void* detour, void** original)
{
    if (vtable == nullptr || detour == nullptr || original == nullptr)
        return false;

    void** slot = &vtable[index];
    if (*slot == detour)
        return true;

    DWORD oldProtect = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        return false;

    *original = *slot;
    *slot = detour;

    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    return true;
}

// ── 纹理 / SRV 记录 ──────────────────────────────────────────────────────

HRESULT STDMETHODCALLTYPE DetourCreateTexture2D(ID3D11Device* self, const D3D11_TEXTURE2D_DESC* desc,
                                                const D3D11_SUBRESOURCE_DATA* initialData,
                                                ID3D11Texture2D** texture)
{
    const auto original = g_origCreateTexture2D;
    const HRESULT result = original(self, desc, initialData, texture);

    if (SUCCEEDED(result) && texture != nullptr && *texture != nullptr && desc != nullptr)
    {
        try
        {
            TextureRegistry::Instance().OnTextureCreated(*texture, *desc, initialData);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }

    return result;
}

HRESULT STDMETHODCALLTYPE DetourCreateShaderResourceView(
    ID3D11Device* self, ID3D11Resource* resource, const D3D11_SHADER_RESOURCE_VIEW_DESC* desc,
    ID3D11ShaderResourceView** view)
{
    const auto original = g_origCreateShaderResourceView;
    const HRESULT result = original(self, resource, desc, view);

    if (SUCCEEDED(result) && view != nullptr && *view != nullptr)
    {
        try
        {
            TextureRegistry::Instance().RememberSrv(*view, resource);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }

    return result;
}

void LogHashOnce(uint32_t hash, UINT slot, const AliasTarget* target, bool isHd)
{
    {
        std::lock_guard<std::mutex> lock(g_logMutex);
        if (g_loggedHashes.size() >= kMaxLoggedHashes || g_loggedHashes.count(hash) != 0)
            return;
        g_loggedHashes.insert(hash);
    }

    const std::string hex = HashToHex(hash);
    if (target != nullptr)
    {
        LogWrite(LogLevel::Info, "bind slot=%u hash=%s match=uhd dds=%s", slot, hex.c_str(),
                 WideToUtf8(target->ddsPath).c_str());
    }
    else
    {
        LogWrite(LogLevel::Info, "bind slot=%u hash=%s match=%s", slot, hex.c_str(),
                 isHd ? "hd" : "none");
    }
}

void InstrumentBindings(UINT start, UINT count, ID3D11ShaderResourceView* const* views)
{
    auto& registry = TextureRegistry::Instance();
    const AliasTable* table = g_aliasTable.load(std::memory_order_acquire);

    for (UINT i = 0; i < count; ++i)
    {
        ID3D11ShaderResourceView* view = views[i];
        if (view == nullptr)
            continue;

        g_binds.fetch_add(1, std::memory_order_relaxed);

        uint32_t hash = 0;
        bool isReplacement = false;
        if (!registry.TryGetSrvHash(view, &hash, &isReplacement))
        {
            // 没登记过（例如 WWMI 内部创建）：回退一次 GetResource 补齐。
            ComPtr<ID3D11Resource> resource;
            view->GetResource(resource.GetAddressOf()); // 返回 void，不是 HRESULT
            registry.RememberSrv(view, resource.Get());

            if (!registry.TryGetSrvHash(view, &hash, &isReplacement))
            {
                g_unknownBinds.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
        }

        g_hashedBinds.fetch_add(1, std::memory_order_relaxed);

        if (isReplacement || table == nullptr)
            continue;

        if (table->IsHdHash(hash))
        {
            g_hdBinds.fetch_add(1, std::memory_order_relaxed);
            LogHashOnce(hash, start + i, nullptr, true);
            continue;
        }

        AliasTarget target;
        if (table->TryGet(hash, &target))
        {
            g_uhdBinds.fetch_add(1, std::memory_order_relaxed);
            LogHashOnce(hash, start + i, &target, false);

            // M2：在这里替换 SRV；M1 只记录。
        }
    }
}

void STDMETHODCALLTYPE DetourPSSetShaderResources(ID3D11DeviceContext* self, UINT start, UINT count,
                                                  ID3D11ShaderResourceView* const* views)
{
    const auto original = g_origPSSetShaderResources;

    if (views != nullptr && count > 0)
    {
        try
        {
            InstrumentBindings(start, count, views);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }

    original(self, start, count, views);
}

HRESULT STDMETHODCALLTYPE DetourMap(ID3D11DeviceContext* self, ID3D11Resource* resource,
                                    UINT subresource, D3D11_MAP mapType, UINT mapFlags,
                                    D3D11_MAPPED_SUBRESOURCE* mapped)
{
    const auto original = g_origMap;
    const HRESULT result = original(self, resource, subresource, mapType, mapFlags, mapped);

    if (SUCCEEDED(result) && mapped != nullptr && mapped->pData != nullptr && subresource == 0)
    {
        try
        {
            RememberMap(resource, subresource, mapped->pData, mapped->RowPitch);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }

    return result;
}

void STDMETHODCALLTYPE DetourUnmap(ID3D11DeviceContext* self, ID3D11Resource* resource,
                                   UINT subresource)
{
    const auto original = g_origUnmap;

    // Unmap 之后映射内存就不再有效，先取记录再调用原函数。
    MapRecord record;
    const bool hasRecord = TakeMap(resource, subresource, &record);

    original(self, resource, subresource);

    if (hasRecord && subresource == 0)
    {
        try
        {
            uint32_t hash = 0;
            if (HashMappedResource(resource, record, &hash))
            {
                TextureRegistry::Instance().SetTextureHash(resource, hash);

                D3D11_TEXTURE2D_DESC desc{};
                if (TextureRegistry::Instance().TryGetTextureDesc(resource, &desc))
                    LogTextureOnce(hash, desc);
            }
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }
}

void STDMETHODCALLTYPE DetourUpdateSubresource(ID3D11DeviceContext* self, ID3D11Resource* resource,
                                               UINT subresource, const D3D11_BOX* box,
                                               const void* data, UINT rowPitch, UINT depthPitch)
{
    const auto original = g_origUpdateSubresource;
    original(self, resource, subresource, box, data, rowPitch, depthPitch);

    // 只认整块 mip0；部分 box 更新无法还原完整 hash。
    if (subresource != 0 || box != nullptr || data == nullptr || rowPitch == 0)
        return;

    try
    {
        D3D11_TEXTURE2D_DESC desc{};
        if (!TextureRegistry::Instance().TryGetTextureDesc(resource, &desc))
            return;

        D3D11_SUBRESOURCE_DATA source{};
        source.pSysMem = data;
        source.SysMemPitch = rowPitch;

        uint32_t hash = 0;
        if (ComputeTextureHash(desc, &source, &hash))
        {
            TextureRegistry::Instance().SetTextureHash(resource, hash);
            LogTextureOnce(hash, desc);
        }
    }
    catch (...)
    {
        g_failures.fetch_add(1, std::memory_order_relaxed);
    }
}

void STDMETHODCALLTYPE DetourCopySubresourceRegion(ID3D11DeviceContext* self,
                                                   ID3D11Resource* destination,
                                                   UINT destinationSubresource, UINT destinationX,
                                                   UINT destinationY, UINT destinationZ,
                                                   ID3D11Resource* source, UINT sourceSubresource,
                                                   const D3D11_BOX* box)
{
    const auto original = g_origCopySubresourceRegion;
    original(self, destination, destinationSubresource, destinationX, destinationY, destinationZ,
             source, sourceSubresource, box);

    if (destinationSubresource == 0 && sourceSubresource == 0)
    {
        try
        {
            TextureRegistry::Instance().CopyTextureHash(destination, source);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }
}

void STDMETHODCALLTYPE DetourCopyResource(ID3D11DeviceContext* self, ID3D11Resource* destination,
                                          ID3D11Resource* source)
{
    const auto original = g_origCopyResource;
    original(self, destination, source);

    try
    {
        TextureRegistry::Instance().CopyTextureHash(destination, source);
    }
    catch (...)
    {
        g_failures.fetch_add(1, std::memory_order_relaxed);
    }
}

void InstallDeviceHooks(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (device == nullptr)
        return;

    ComPtr<ID3D11DeviceContext> immediate;
    if (context == nullptr)
    {
        device->GetImmediateContext(immediate.GetAddressOf()); // 返回 void
        context = immediate.Get();
    }

    void** deviceVtable = *reinterpret_cast<void***>(device);
    const bool textureHook = PatchVtableSlot(deviceVtable, 5,
                                             reinterpret_cast<void*>(&DetourCreateTexture2D),
                                             reinterpret_cast<void**>(&g_origCreateTexture2D));
    const bool srvHook = PatchVtableSlot(deviceVtable, 7,
                                         reinterpret_cast<void*>(&DetourCreateShaderResourceView),
                                         reinterpret_cast<void**>(&g_origCreateShaderResourceView));

    int contextHooks = 0;
    if (context != nullptr)
    {
        void** contextVtable = *reinterpret_cast<void***>(context);

        // ID3D11DeviceContext 方法在 vtable 中的位置（前面是 IUnknown + DeviceChild 共 7 个）：
        // 8 PSSetShaderResources、15 Map、16 Unmap、17 CopySubresourceRegion、18 CopyResource、19 UpdateSubresource。
        contextHooks += PatchVtableSlot(contextVtable, 8,
                                        reinterpret_cast<void*>(&DetourPSSetShaderResources),
                                        reinterpret_cast<void**>(&g_origPSSetShaderResources))
                            ? 1
                            : 0;
        contextHooks += PatchVtableSlot(contextVtable, 15, reinterpret_cast<void*>(&DetourMap),
                                        reinterpret_cast<void**>(&g_origMap))
                            ? 1
                            : 0;
        contextHooks += PatchVtableSlot(contextVtable, 16, reinterpret_cast<void*>(&DetourUnmap),
                                        reinterpret_cast<void**>(&g_origUnmap))
                            ? 1
                            : 0;
        contextHooks += PatchVtableSlot(contextVtable, 17,
                                        reinterpret_cast<void*>(&DetourCopySubresourceRegion),
                                        reinterpret_cast<void**>(&g_origCopySubresourceRegion))
                            ? 1
                            : 0;
        contextHooks += PatchVtableSlot(contextVtable, 18,
                                        reinterpret_cast<void*>(&DetourCopyResource),
                                        reinterpret_cast<void**>(&g_origCopyResource))
                            ? 1
                            : 0;
        contextHooks += PatchVtableSlot(contextVtable, 19,
                                        reinterpret_cast<void*>(&DetourUpdateSubresource),
                                        reinterpret_cast<void**>(&g_origUpdateSubresource))
                            ? 1
                            : 0;
    }

    LogWrite(LogLevel::Info,
             "device created vtable=%p context=%p deviceHooks=%d/%d contextHooks=%d/6",
             deviceVtable, context, textureHook ? 1 : 0, srvHook ? 1 : 0, contextHooks);
}

HRESULT WINAPI DetourCreateDeviceAndSwapChain(
    IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, HMODULE software, UINT flags,
    const D3D_FEATURE_LEVEL* levels, UINT levelCount, UINT sdkVersion,
    const DXGI_SWAP_CHAIN_DESC* swapChainDesc, IDXGISwapChain** swapChain, ID3D11Device** device,
    D3D_FEATURE_LEVEL* chosenLevel, ID3D11DeviceContext** context)
{
    const auto original = g_origCreateDeviceAndSwapChain;
    const HRESULT result = original(adapter, driverType, software, flags, levels, levelCount,
                                    sdkVersion, swapChainDesc, swapChain, device, chosenLevel,
                                    context);

    if (SUCCEEDED(result) && device != nullptr && *device != nullptr)
    {
        try
        {
            InstallDeviceHooks(*device, context != nullptr ? *context : nullptr);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }

    return result;
}

HRESULT WINAPI DetourCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, HMODULE software,
                                  UINT flags, const D3D_FEATURE_LEVEL* levels, UINT levelCount,
                                  UINT sdkVersion, ID3D11Device** device,
                                  D3D_FEATURE_LEVEL* chosenLevel, ID3D11DeviceContext** context)
{
    const auto original = g_origCreateDevice;
    const HRESULT result = original(adapter, driverType, software, flags, levels, levelCount,
                                    sdkVersion, device, chosenLevel, context);

    if (SUCCEEDED(result) && device != nullptr && *device != nullptr)
    {
        try
        {
            InstallDeviceHooks(*device, context != nullptr ? *context : nullptr);
        }
        catch (...)
        {
            g_failures.fetch_add(1, std::memory_order_relaxed);
        }
    }

    return result;
}

}  // namespace

bool InstallD3D11Hooks()
{
    if (g_installed.load(std::memory_order_acquire))
        return true;

    HMODULE d3d11 = GetModuleHandleW(L"d3d11.dll");
    if (d3d11 == nullptr)
    {
        LogWrite(LogLevel::Warn, "d3d11.dll not loaded; mapper idle (DX12?)");
        return false;
    }

    const auto createDeviceAndSwapChain =
        GetProcAddress(d3d11, "D3D11CreateDeviceAndSwapChain");
    const auto createDevice = GetProcAddress(d3d11, "D3D11CreateDevice");
    if (createDeviceAndSwapChain == nullptr && createDevice == nullptr)
    {
        LogWrite(LogLevel::Warn, "D3D11 create exports not found");
        return false;
    }

    if (MH_Initialize() != MH_OK)
    {
        LogWrite(LogLevel::Error, "MH_Initialize failed");
        return false;
    }

    if (createDeviceAndSwapChain != nullptr)
    {
        const MH_STATUS status = MH_CreateHook(
            reinterpret_cast<LPVOID>(createDeviceAndSwapChain),
            reinterpret_cast<LPVOID>(&DetourCreateDeviceAndSwapChain),
            reinterpret_cast<LPVOID*>(&g_origCreateDeviceAndSwapChain));
        if (status != MH_OK && status != MH_ERROR_ALREADY_CREATED)
        {
            LogWrite(LogLevel::Error, "MH_CreateHook(D3D11CreateDeviceAndSwapChain) status=%d",
                     static_cast<int>(status));
            return false;
        }
    }

    if (createDevice != nullptr)
    {
        const MH_STATUS status = MH_CreateHook(
            reinterpret_cast<LPVOID>(createDevice), reinterpret_cast<LPVOID>(&DetourCreateDevice),
            reinterpret_cast<LPVOID*>(&g_origCreateDevice));
        if (status != MH_OK && status != MH_ERROR_ALREADY_CREATED)
        {
            LogWrite(LogLevel::Error, "MH_CreateHook(D3D11CreateDevice) status=%d",
                     static_cast<int>(status));
            return false;
        }
    }

    if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
    {
        LogWrite(LogLevel::Error, "MH_EnableHook failed");
        return false;
    }

    g_installed.store(true, std::memory_order_release);
    LogWrite(LogLevel::Info, "export hooks installed module=%p swapchain=%p device=%p", d3d11,
             createDeviceAndSwapChain, createDevice);
    return true;
}

void SetAliasTableForHooks(const AliasTable* table)
{
    g_aliasTable.store(table, std::memory_order_release);
}

void SetApplyMode(bool apply)
{
    g_applyMode.store(apply, std::memory_order_release);
}

bool HooksInstalled()
{
    return g_installed.load(std::memory_order_acquire);
}

HookCounters GetHookCounters()
{
    HookCounters counters;
    counters.binds = g_binds.load(std::memory_order_relaxed);
    counters.hashedBinds = g_hashedBinds.load(std::memory_order_relaxed);
    counters.unknownBinds = g_unknownBinds.load(std::memory_order_relaxed);
    counters.hdBinds = g_hdBinds.load(std::memory_order_relaxed);
    counters.uhdBinds = g_uhdBinds.load(std::memory_order_relaxed);
    counters.failures = g_failures.load(std::memory_order_relaxed);
    counters.substitutions = g_substitutions.load(std::memory_order_relaxed);
    counters.updatedHashes = TextureRegistry::Instance().UpdatedHashCount();
    return counters;
}

}  // namespace jasm::uhd
