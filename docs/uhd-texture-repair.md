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

## 3. UHD 推荐哈希修复

- 复用内置 `wuwa-mod-cli`（Wuwa_Mod_Fixer），参数为 `--derived-hashes`（补全贴图状态：LOD Bias High/Medium 的派生哈希）。
- 与「应用稳定纹理」（`--stable-texture`）互斥；按钮会自动关掉稳定纹理再开派生哈希。
- 只处理列表里**已勾选**的 mod；修复前按现有机制备份为 `.BEFORE_<日期>_hashfix`。
- 上游没有单独的“UHD 开关”，UHD 差异就是靠派生哈希覆盖的。

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
