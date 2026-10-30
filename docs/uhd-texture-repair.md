# 鸣潮 UHD 贴图修复（JASM 浮窗）

> 适用范围：游戏资源档位为 **UHD（极致）** 时出现“贴图错乱 / 糊 / 缺失 / 变回原版”的第三方 mod。
> 入口：浮窗 →「修复」页 →「UHD 贴图修复」。
> 边界：只做文件层修复（哈希引用 + 游戏配置），**不注入、不改游戏内存、不压缩贴图**。

## 1. 两类错误，别修错方向

| 现象 | 根因 | 用哪个按钮 |
|---|---|---|
| 贴图错位、紫块、糊、某部位贴图不对 | mod 的贴图 hash 与当前游戏/UHD 资源包不匹配（LOD Bias High/Medium 的派生哈希过期） | **UHD 推荐哈希修复** |
| 整体贴图被降级、远处糊、光照/光追下异常 | 游戏流送（Streaming）/光追把 mod 贴图降采样或截断 | **应用配置修复** |

两类可以叠加：先应用配置修复，再跑 UHD 推荐哈希修复。

## 2. 档位检测

- 游戏 3.7+ 把资源放在 `Client\Content\{SD,HD,UHD}`，JASM 检测哪个目录下有实际文件：
  - 只装一个档位 → 自动选中；
  - 多个档位都有 → 提示“请按官方启动器当前选择手动指定”（选错会让 UE4 崩）；
  - 找不到 `Client\Content` → 提示手动选择。
- 启动参数为 `-krqlv=SD|HD|UHD`（社区工具约定）；UI 里的档位选择会持久化，后续「直启 + 注入」会带上它。
- **档位必须与官方启动器一致**，JASM 只记录、不修改启动器的资源选择。

## 3. 执行 UHD 修复（三种方式）

按目标 mod 选，**普通 mod 默认用基础哈希**：

| 方式 | CLI 参数 | 适用 |
|---|---|---|
| 基础哈希修复（默认） | 无 | 普通 WWMI mod 的 `hash =` 过期；最安全 |
| 稳定纹理修复 | `--stable-texture` | 部分角色，且需要**最新 RabbitFX** |
| 补全状态贴图（RabbitFX） | `--derived-hashes` | 只给依赖 RabbitFX 的状态贴图（湿身、千咲强化 E 等）补绑定；普通 mod 加了可能变绿 |

- 只处理列表里**已勾选**的 mod；修复前按现有机制备份 `.BAK`（可回滚）。
- 基础哈希与稳定纹理/派生哈希是不同作用面，不是“越全越好”。
- 如果三种都试过仍绿，通常是 mod 本身不支持 UHD 资源包（贴图/着色器按 HD 制作），只能换 HD 档位或等 mod/上游更新；JASM 不做内存注入。

## 3.1 从 Rocket RoleHash 学习库导入（UHD hash 表未覆盖时）

新版 Rocket（`Lod0_texture.dll` 5.96 MB，2026-10 更新）内置 **RoleHash 自动学习**：运行时观察游戏实际绑定的
HD/UHD 贴图 hash，写入 `%LOCALAPPDATA%\Rocket\Wuwa\AutoLearning\RoleHash.aliases.tsv`。

JASM 的「导入 Rocket UHD 映射」按钮只**读取这份学习结果**，把 mod 的 HD 覆盖段补成 UHD 覆盖段：

```ini
[TextureOverrideTexture8_UHD_dd1f9bc8]
hash = dd1f9bc8
match_priority = 0
if $object_detected
    this = ResourceDiffuse
endif
```

**前提（必须按顺序）**
1. 在浮窗里勾选**一个**目标 Mod，点「UHD 学习向导 → 开始 UHD 学习」：
   JASM 会只启用这个 Mod（其余停用），并启动**新版 Rocket**（`Lod0_texture.dll` ≥ 5 MB 那份）。
   注意：必须让**目标 mod 处于启用状态**再学；原版角色只能学到游戏原生 hash，配对不出 mod 的 HD hash。
2. 在 Rocket 里以 **UHD** 启动游戏，让角色穿着这个 Mod 出现、保持一段时间，等
   `learning-status.tsv` 里该角色的 `uhd_ready` 增长。
3. **完全退出游戏和 Rocket**，回浮窗点「完成学习并导入」：
   JASM 恢复原来的 Mod 启用状态，读取学习库，只给所选 Mod 生成 UHD 覆盖段（每个 ini 先备份为
   `.BEFORE_<时间戳>_uhd.bak`），然后发 F10。
4. 换下一个没修好的 Mod，重复 1–3。Rocket 与 JASM 共用同一个 `Mods` 文件夹，不需要把 mod 另加到 Rocket。
5. 日常启动/切换请用 **XXMI Launcher / JASM**，不要从 Rocket 启动——Rocket 的 DLL 会运行时“钉住”它学过的
   mod，和 JASM 的 DISABLED_ 切换冲突。

**局限**
- 学习结果绑定“学习时启用的那个 mod”的 HD hash；每个覆盖不全的 mod 需要单独学习一次。
- 不要在 JASM 日常启动流程里注入 Rocket 的 DLL；两者会抢同一个角色的贴图。
- 本文只描述文件层修复；不注入、不修改游戏内存。

## 4. 应用配置修复（XXMI 方式）

按 Rocket 的 XXMI 配置方式，备份后写：

`Client\Saved\Config\WindowsNoEditor\Engine.ini`
```ini
[/Script/Engine.RendererSettings]
r.RayTracing.LoadConfig=0
r.MaxAnisotropy=1
r.Streaming.HiddenPrimitiveScale=20
```

`Client\Saved\Config\WindowsNoEditor\GameUserSettings.ini`
```ini
[/Script/Engine.GameUserSettings]
ImageDetail=3
ImageQuality=3
RayTracing=0
RayTracedReflection=0
RayTracedGI=0
```

- 只改上述键；注释、其它键、其它段原样保留。
- **会强制高画质**，低配机型可能掉帧；不想用时点「恢复配置备份」。
- 备份位于 `%LOCALAPPDATA%\JASM\UhdRepairBackups\<时间戳>\`，含 `manifest.json`；恢复只针对最近一次。
- 写入/恢复后需要**重启游戏**生效（不是 F10 重载）。

## 5. 与 Rocket 的对应关系（静态分析结论）

| Rocket 行为 | JASM 对应 |
|---|---|
| 资源档位 SD/HD/UHD 识别，选错停启动 | JASM 自动检测 + 手动选择，仅提示 |
| `贴图修复方法` = XXMI：改配置文件，影响所有模型贴图 | 「应用配置修复」写 Engine.ini/GameUserSettings.ini |
| `贴图修复方法` = 内存注入 `Lod0_texture.dll`，只改主控角色 | **不做**（内存注入/游戏逻辑修改超出边界） |
| 大贴图压缩（DirectXTex，BC7/BC6H，降到 4K） | **不做**（与本功能无关） |

## 6. 排错

- 提示“未找到游戏配置目录”：游戏可能装在非标准位置，或 exe 不在 `Client\Binaries\Win64` 下；请确认游戏路径。
- 写入失败（拒绝访问）：游戏目录可能需要管理员权限；以管理员身份运行 JASM 后重试。
- 配置已应用但贴图仍错：执行「UHD 推荐哈希修复」；仍不行则用现有「回滚」恢复 mod，再检查 mod 是否本身缺文件。
- 改完想还原：先「恢复配置备份」，mod 侧用修复页的「备份回滚」。
