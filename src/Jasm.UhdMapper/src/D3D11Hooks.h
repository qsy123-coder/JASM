#pragma once

#include <cstdint>

namespace jasm::uhd {

class AliasTable;

// 由工作线程在 ResumeThread 之前调用：MinHook 挂 d3d11.dll 的两个创建导出，
// 拿到 device/context 后再替换它们 vtable 里的目标方法。
bool InstallD3D11Hooks();

void SetAliasTableForHooks(const AliasTable* table);

// M2 才使用；M1 恒为 false（只记录不替换）。
void SetApplyMode(bool apply);

bool HooksInstalled();

struct HookCounters
{
    uint64_t binds = 0;
    uint64_t hashedBinds = 0;
    uint64_t unknownBinds = 0;
    uint64_t hdBinds = 0;
    uint64_t uhdBinds = 0;
    uint64_t failures = 0;
    uint64_t substitutions = 0;
};

HookCounters GetHookCounters();

}  // namespace jasm::uhd
