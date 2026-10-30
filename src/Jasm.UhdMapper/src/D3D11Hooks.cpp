#include "D3D11Hooks.h"

#include <atomic>
#include <mutex>
#include <string>
#include <unordered_set>

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

CreateDeviceAndSwapChainFn g_origCreateDeviceAndSwapChain = nullptr;
CreateDeviceFn g_origCreateDevice = nullptr;

CreateTexture2DFn g_origCreateTexture2D = nullptr;
CreateShaderResourceViewFn g_origCreateShaderResourceView = nullptr;
PSSetShaderResourcesFn g_origPSSetShaderResources = nullptr;

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
            if (SUCCEEDED(view->GetResource(resource.GetAddressOf())))
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

void InstallDeviceHooks(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (device == nullptr)
        return;

    ComPtr<ID3D11DeviceContext> immediate;
    if (context == nullptr)
    {
        if (SUCCEEDED(device->GetImmediateContext(immediate.GetAddressOf())))
            context = immediate.Get();
    }

    void** deviceVtable = *reinterpret_cast<void***>(device);
    const bool textureHook = PatchVtableSlot(deviceVtable, 5,
                                             reinterpret_cast<void*>(&DetourCreateTexture2D),
                                             reinterpret_cast<void**>(&g_origCreateTexture2D));
    const bool srvHook = PatchVtableSlot(deviceVtable, 7,
                                         reinterpret_cast<void*>(&DetourCreateShaderResourceView),
                                         reinterpret_cast<void**>(&g_origCreateShaderResourceView));

    bool contextHook = false;
    if (context != nullptr)
    {
        void** contextVtable = *reinterpret_cast<void***>(context);
        contextHook = PatchVtableSlot(contextVtable, 8,
                                      reinterpret_cast<void*>(&DetourPSSetShaderResources),
                                      reinterpret_cast<void**>(&g_origPSSetShaderResources));
    }

    LogWrite(LogLevel::Info, "device created vtable=%p context=%p deviceHooks=%d/%d contextHook=%d",
             deviceVtable, context, textureHook ? 1 : 0, srvHook ? 1 : 0, contextHook ? 1 : 0);
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
    return counters;
}

}  // namespace jasm::uhd
