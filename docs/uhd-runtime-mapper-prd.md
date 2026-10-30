# 鸣潮 UHD 运行时贴图映射 DLL PRD

## 背景与问题

鸣潮在 UHD 资源档位下，部分 mod 的贴图会变成白色/绿色/错位。根因不是 mod 缺文件，而是：

- mod 用 `[TextureOverrideTexture*] hash = <HD hash>` 绑定自己的 HD 贴图；
- UHD 资源包让游戏绑定的是**另一个 UHD hash**，mod 的 HD 覆盖不命中；
- 上游 Wuwa_Mod_Fixer 的静态哈希表覆盖不到这些 UHD hash（实测每个 mod 只有约 16–19 条命中）。

Rocket 的 UHD 修复本质是**运行时 RoleHash**：观察游戏实际绑定的 HD/UHD hash，按角色/组件/槽位配对，再在 draw 时把 UHD hash 的绑定重定向到当前 mod 的 **HD hash 覆盖资源**。它的学习结果写在
`%LOCALAPPDATA%\Rocket\Wuwa\AutoLearning`，且**按当前启用的 mod 学习**——这解释了为什么现有“离线导入 aliases”仍需每个 mod 学一次。

> **术语说明（重要）**：Rocket 数据里的 `UHD-to-HD` 指的是 **UHD hash → mod 的 HD hash 覆盖**，不是把游戏资源包切到 HD，也不是把贴图重采样成 HD。
> 游戏始终以 UHD 档位运行；环境/其他角色/未改贴图仍是 UHD。被 mod 覆盖的槽位使用 **mod 自带 DDS 的原始分辨率**（1K/2K/4K 取决于作者），本方案不做降采样，也不做 4K 拉伸。

## 目标 / 非目标

**目标**
- 任意切换 mod 后，UHD 下的白色/绿色/错位贴图自动修复，**不需要为每个 mod 手动学习**。
- 修复在运行时完成，不修改 mod 文件、不要求用户理解哈希。
- **不改变游戏资源档位（仍 UHD）、不做全局降级、不做贴图重采样**；只重定向被 mod 覆盖的槽位，使用 mod 自带 DDS 的原始分辨率。
- 与 JASM 的 mod 启用/停用（`DISABLED_`）和 F10 重载协同。

**非目标（首期明确不做）**
- 不强制 LOD0/mip 常驻、不改 `Engine.ini`/`GameUserSettings.ini` 画质配置。
- 不做过检/反检测、不改游戏逻辑、不断网、不做 PEB 摘除。
- 不注入 Rocket 的 `Lod0_texture.dll`（它会钉住学过的 mod，和 JASM 切换冲突）。
- 不改动 Wuwa_Mod_Fixer 的哈希表数据。

## 用户故事

1. 作为玩家，我在 JASM 里从 mod A 切到 mod B，进游戏后 B 的贴图正常，不需要先“学习 B”。
2. 作为玩家，游戏以 UHD 启动时，我不需要手动跑 Rocket 或抓帧。
3. 作为玩家，如果某个 mod 的贴图无法映射，我能看到状态提示，而不是一片白/绿。
4. 作为维护者，我能在不重新编译主程序的情况下更新映射规则/修补签名。

## 方案设计

### 方案 A：自研运行时映射 DLL（推荐）

**启动链**
`JASM（中完整性）→ Elevator（提权）→ CreateProcessW(CREATE_SUSPENDED) → 注入 WWMI 的 d3d11.dll → 注入 Jasm.UhdMapper.dll → ResumeThread`。

复用现有：
- `GameInstallPathDetector` 定位游戏 exe；
- `ModEnvSetupFacade` 定位 `<XXMI>\WWMI\d3d11.dll`；
- `ElevatorLaunchInjectProtocol`（已加入 Core）扩展为注入两个 DLL；
- `CommandHandlerService` 的 game-start 分支改为走直启注入，失败回退 XXMI Launcher；
- `CharacterModList.Enable/Disable` 与 `OverlayRefreshCoordinator` 的 F10 刷新。

**运行时 DLL（Jasm.UhdMapper.dll，x64 C++）**
1. 用 MinHook（或手写 inline hook）挂 `ID3D11DeviceContext::PSSetShaderResources` / `DrawIndexed`；
2. 用 UE 反射（GObjects + 角色/组件锚点）识别当前主控角色与画到的组件/槽位；
3. 读取 JASM 生成的 `active-mod.json`（角色、当前 mod、HD hash → DDS 路径/Resource 名）；
4. **自动学习**：同一角色/组件/槽位下，把 mod 的 HD hash 覆盖与游戏 UHD 绑定配对，缓存 `UHD hash → mod DDS`；
5. **draw 时映射**：检测到 UHD hash 时，将 SRV 替换为 DLL 自己用 DirectXTex 从 mod DDS 创建的纹理（按需创建并缓存）；
6. 监听 `active-mod.json` 变化（JASM 切换 mod / F10）刷新映射；无需重启游戏。

**切换体验**：JASM 切换 mod → 写 manifest + 发 F10 → 3DMigoto 重载 mod → DLL 刷新并继续映射。目标是一句话：**切任何 mod 都自动修**。

### 方案 B：注入 Rocket 的 `Lod0_texture.dll`

- 效果与 Rocket 一致，但该 DLL 会运行时钉住它学过的 mod；JASM 切 mod 后仍显示旧贴图。
- 否决：不满足“切任何 mod 都自动修”。

### 方案 C：继续离线导入 aliases（现状）

- 需要每个 mod 用 Rocket 学一次；不满足目标。
- 否决：作为回退/种子保留。

### 取舍

选 A。它是唯一能同时满足“无需 per-mod 学习”和“JASM 切换”的方案；代价是需要原生 DLL、CI MSVC 构建和 D3D11/UE 反射调试。

## 数据与接口变更

- JASM 写 `%LOCALAPPDATA%\JASM\UhdRuntime\active-mod.json`：角色、mod 名、`HD hash → DDS 路径/Resource`、生成时间。
- DLL 写 `%LOCALAPPDATA%\JASM\UhdRuntime\{role}.cache.json`：学习到的 `UHD hash → mod DDS` 配对，供下次启动直接复用。
- 控制通道：命名管道 `\\.\pipe\Jasm.UhdMapper`（JASM → DLL：reload / status；DLL → JASM：映射数/错误）。
- `ElevatorLaunchInjectProtocol` 扩展：一次启动注入 `d3d11.dll` 与 `Jasm.UhdMapper.dll`；校验 DLL 路径/名称。
- 新增项目 `src/Jasm.UhdMapper/`（C++ x64），CI `windows-latest` 构建并作为资源嵌入主程序。

## 边界与异常

- 游戏更新导致 GObjects/偏移失效：DLL 自动扫描失败时**不崩游戏**，只禁用映射并在状态里报错；保留离线导入回退。
- DX12 启动：WWMI 与映射 DLL 都不生效；检测并提示使用 DX11。
- 反作弊/ToS：只做渲染层贴图替换，不挂钩反作弊、不改游戏逻辑；仍提示用户风险。
- DLL 崩溃：注入前后做版本/签名校验；DLL 内异常捕获，失败即卸载/禁用映射。
- 无配对：保持原贴图并显示“未学习/未匹配”，不强制替换错误资源。
- 显存：纹理按需创建并设上限；切换 mod 时释放旧缓存。

## 验收标准

- [ ] JASM 直启 UHD，游戏正常进入，WWMI mod 生效。
- [ ] 从 mod A 切到 mod B（不重启游戏，仅 F10），B 的白/绿/错图消失。
- [ ] 不需要对 B 执行任何“学习”操作。
- [ ] 映射失败时游戏不崩，状态行给出可行动原因。
- [ ] 关闭 UHD 修复开关后，行为回到 XXMI Launcher 原路径。
- [ ] 同一 mod 二次启动直接命中缓存，无重复学习。
- [ ] 不修改 mod 文件；回滚只需关闭开关/删除缓存。

## 测试计划

**单元/集成（Core）**
- active-mod manifest 生成/校验（hash、路径、DS 文件名、转义）。
- 缓存读写、版本失效、损坏降级。
- Elevator 注入协议（双 DLL、路径校验、失败回收）。

**DLL**
- 在测试 D3D11 程序上验证 hook 不破坏渲染、SRV 替换 refcount 正确。
- 合成 DDS（BC1/BC3/BC7）加载与尺寸/格式校验。
- 角色/组件识别在游戏更新前后的降级路径。

**手工（真实鸣潮 UHD）**
- 两个 mod 来回切 10 次，贴图始终正确。
- 游戏更新后首次启动：能识别/降级，不崩。
- 高显存 4K 贴图切换：无泄漏/卡死。

## 分期

1. **Phase 1（最小闭环）**：JASM 直启 + 注入 WWMI d3d11 + 空壳 mapper DLL；验证注入时序、游戏可进、WWMI 正常。
2. **Phase 2（单 mod 映射）**：读取 active-mod manifest，hook SRV，按已知 HD/UHD 配对替换；验证一个 mod 白/绿消失。
3. **Phase 3（自动学习 + 切换）**：运行时配对、缓存、监听 mod 切换/F10；达到验收标准。
4. **Phase 4（硬化/发布）**：异常捕获、版本兼容、CI 构建与嵌入、文档与回滚。

## 风险与回滚

- 原生 DLL 调试周期长（本机无 MSVC，只能 CI 构建）；先用 CI artifact 迭代。
- D3D11/UE 内部结构随游戏更新变化；必须设计降级与快速修补路径。
- 反作弊可能对注入敏感；默认关闭、显式开关、只做渲染映射。
- 回滚：关闭“UHD 运行时修复”开关 → 启动命令回到 XXMI Launcher；删除 `%LOCALAPPDATA%\JASM\UhdRuntime` 即清除缓存。
