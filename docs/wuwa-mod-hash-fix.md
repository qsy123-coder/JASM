# 鸣潮 WWMI Mod 哈希修复手册

> 适用范围：XXMI / WWMI 下「游戏更新后失效」的第三方 mod（实例均取自「改岸宝」系列）。
> 注意与 `CLAUDE.md` 的「JASM 自动更新 / Release 发布链路」区分：那节讲 **JASM 自身**的更新链路，本文讲**修用户的 mod**。

## 0. 先判型：两种「过期」，别修错方向

| 现象 | 根因 | 判据 |
|---|---|---|
| 整个人还是原版模型 | `[TextureOverrideComponentN]` 的 `hash`（匹配游戏 vb0）过期 | dump 里游戏 draw 绑的 vb0 ≠ mod.ini 里写的 |
| **模型变了，贴图还是原版**；或某部位糊 / 斑驳 / 错位 | 组件哈希是对的，**贴图哈希过期** | vb0 相等，但 `[TextureOverrideTextureN]` 的 hash 跟游戏当前绑的一个都不撞 |

第二种是本文要修的。**动手前先确认第一种没发生**，否则修贴图是白费。

## 1. 先查现成表（能查表就别手修）

社区工具「鸣潮 mod woju 修复器」带哈希映射表。本机两份 asset，**以文件日期为准，别信版本号**：

- `…\MC-MOD整合包\wMOD全集-每日更新\鸣潮mod woju修复器v3.7.1.0以及asset文件\Assets v3.7.1.0.zip` → 内层文件 9/1
- `…\MC-MOD整合包\wMOD全集-每日更新\鸣潮mod woju修复器v3.7.0.2以及asset文件\` → **10/2，比上面新**

四个 json 的用途：

| 文件 | 内容 |
|---|---|
| `hashes.json` | 按「角色+形态」分组的 `旧哈希 → 新哈希` 链，可链式套用 |
| `StableHashes.json` | 角色 → 逐 component 的 diffuse / lightmap / normal 当前哈希 + `lastVerifiedGameVersion` |
| `HighLodHashes.json` | base / lod_high 两档（只覆盖少数角色） |
| `Derived.json` | 派生哈希 |

**覆盖边界**：表里只有作者做过的角色。例：心月狐 2026-09-11 上线，连 10/2 那版表里都没有 → 修复器对它天然空操作，只能手修。查法：把 mod.ini 里所有哈希拿去 `hashes.json` 里找「作为旧值」出现过的，逐条替换；一条都搜不到就说明这份 mod 的哈希代际早于表能覆盖的范围。

## 2. 手修流程（六步）

**① 开 dump**：小键盘 `0` 开 hunting → 让目标角色 / 物件出现在画面上 → `F8`。产物在 `D:\XXMI\WWMI\FrameAnalysis-<时间戳>\`。前置环境见第 5 节。

**② 认对象**：mod.ini 的 `global $object_guid` = dump 里某组 draw 的「各 component 的 IndexCount 之和」（新版 WWMI Tools 的约定），用 `DrawIndexed(IndexCount:N, StartIndexLocation:M)` 反查。

**③ 读「每个 component 的每个槽位绑了哪张图」**：`log.txt` 里形如

```
000052 3DMigoto Dumping Texture2D …\000052.2-[ShaderRegex_RabbitFX_Main]-ps-t2.dsc -> …\deduped\<hash>-<格式>.dsc
```

的行给出 `帧号 → 槽号 → 哈希`，再把帧号对回该帧的 component。**这一步是全部工作的基础**。

**④ 给 mod 的贴图按格式分类**（读 DDS 头，别看文件名后缀）：

| 格式 | 典型角色 |
|---|---|
| BC7_UNORM_SRGB（DXGI 99） | 漫反射 |
| BC3_UNORM（DXT5） | lightmap / 遮罩 |
| BC7_UNORM（DXGI 98） | 法线 / mask |
| B8G8R8A8 / R8_UNORM / BC1 | LUT / 特效 / 闪点 |

**⑤ 用图像本身配对**：这系列 mod 的贴图**其实就是作者从游戏里导出的原贴图**，所以直接比图最快：

- `Pillow`（10.x，自带 BC7 解码）能直接读 DDS；**Windows 的 WIC 读不了带 DX10 头的 BC7**，别走那条路
- 两边降到 128×128 灰度算相关系数：**1.000 = 同一张图**，直接定案
- 相关系数低的改用**像素统计**（mean / std）比「内容量」，见判据 3

**⑥ 改 ini**：只改 `[TextureOverrideTextureN]` 的 `hash =` 一行（文件名里的 `t=` 不影响匹配）；多个槽要共用同一张图就**新增**一段 `this = ResourceTextureM`。改前备份成 `mod.ini.BEFORE_<日期>_hashfix`。

## 3. 四个判据（按可靠性排序）与两条戒律

1. **相关系数 1.000** —— 同一张图，铁证。
2. **格式类别必须一致** —— 漫反射槽贴法线图必乱，类别按 ④ 的表。
3. **像素统计**：`std≈0` 的**纯色图**（本系列常见 `(0,74,0)` 绿、`(255,0,126)`、全黑）**绝不能贴到游戏现在有内容的槽**（std 高）——会把该部位细节抹平成死板一块。反过来，游戏本身就是纯色的槽，贴纯色图才对。
4. **`ps-t2` 这类槽在游戏里有「第二个变体哈希」**（同一槽在不同帧 / 状态下绑不同的图，形如 LOD 或形态切换）。**两个变体都要指到同一张 mod 图**；只配一个，漏掉的那个变体就是原版贴图套在 mod 的 UV 上 → 症状正是「某块糊 / 斑驳」。

**两条戒律**：

- **作者替换列表里没有的哈希，不要自作聪明去绑。** 例：`4bee4070` 是跨角色共用的全局图，作者的 mod.ini 里根本没有它 → 不该由这个 mod 替换（曾凭「统计最接近」给它绑了张盔甲图，是错的）。
- **哈希按对象各算各的**，同一个包里不同子 mod（不同 guid）之间不能照搬。「改岸宝」系列虽然共用同一套岸宝网格（`drawindexed = 12846 / 18207 / 13392 / 1686 …` 跨角色一致），贴图哈希却各是各的。

## 4. 验证与迭代

改完让用户在游戏里看，然后按**「部位 + 坏法」**反馈，对照「component → 部位」表定位。心月狐的实测对照：

| component | 部位 | 依据 |
|---|---|---|
| comp0 / comp1 | 身体绒毛 / 头发（含辫子） | 漫反射贴图形态 |
| comp2 | 脸 | 贴图上是五官 |
| comp3 | 腿 + 靴子 | 贴图上是皮肤 + 黑色靴型 |
| comp5 | 裙摆 / 服装 | 红白布料褶皱 |
| comp7 | 配饰层 | 金属饰件 + 蓝布 |
| comp6 | 带闪点的小件 | 闪点噪声图 + 柔和法线 |
| comp4 | 一颗红金宝珠（该 comp 只有 1428 索引） | 贴图上是发光宝珠 |

「糊」多半是：① 纯色图贴在游戏有内容的槽（判据 3）② 漏配第二变体（判据 4）③ 把作者原图退回原版哈希、让游戏贴图套在 mod 的 UV 上。**「退回原版」不是安全牌**，它和贴错图一样会出问题。

## 5. 前置环境（hunting）

- hunting 由 XXMI 启动器管：改 `D:\XXMI\XXMI Launcher Config.json` 的 `Importers.WWMI.Migoto.enable_hunting`，**且必须在启动器完全关闭时改**（启动器运行时改，退出时会被内存里的旧值覆盖回去）
- **NumLock 必须开**，否则小键盘 0 发出的虚拟键是 `VK_INSERT`，hunting 根本切不动
- `d3dx.ini` 的 `analyse_options` 用 `dump_ib dump_tex buf txt` 即可（读 vb0 哈希靠 `log.txt`，不需要 `dump_vb`）

## 6. 已知边界

- 只覆盖「组件哈希正确、贴图哈希过期」这一类。组件哈希也过期时要先修它（匹配 vb0 而非 IB，是另一套流程）。
- 游戏里某槽绑的图若在 mod 包里**没有同格式的对应图**（作者没导出，如 BC1 / BC5U 槽），只能保持原版。
- 包本身可能缺文件（例：绯雪包里 `Components-4 t=1772002f.dds` 在作者的原始压缩包里就不存在），这类作者侧的打包缺陷补不了。
