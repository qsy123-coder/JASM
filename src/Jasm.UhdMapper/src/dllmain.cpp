// Jasm.UhdMapper —— 由提权助手在游戏进程 CREATE_SUSPENDED 时注入的 UHD 贴图映射 DLL。
//
// 职责边界（详见 README.md 与 docs/uhd-runtime-mapper-prd.md）：
//   · DllMain 只做最小初始化，然后拉起工作线程；
//   · 工作线程读取 active-mod.json、安装 D3D11 钩子、维护别名表并写日志；
//   · 任何失败都只禁用映射，绝不崩溃游戏。

#include <Windows.h>

#include "MapperApp.h"

// 与托管侧 UhdActiveModManifest.CurrentSchemaVersion 对齐的 ABI 版本（M1 = 2）。
extern "C" __declspec(dllexport) int JasmUhdMapperAbiVersion()
{
    return 2;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        // 注入发生在挂起进程的加载期：这里不能做文件 IO / 加载库，全部交给工作线程。
        jasm::uhd::MapperApp::Instance().Start();
        break;

    case DLL_PROCESS_DETACH:
        // 进程退出时不做任何 COM 释放；仅在被 FreeLibrary 时停掉工作循环。
        if (reserved == nullptr)
            jasm::uhd::MapperApp::Instance().Stop();
        break;

    default:
        break;
    }

    return TRUE;
}
