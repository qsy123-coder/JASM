# Mod 市场页面实现计划

## 概述

为 JASM 桌面应用新增一个 "Mod 市场" (Mod Market) 浏览页面。用户可以从导航栏进入此页面，浏览从 Supabase 数据库中获取的 mod 列表。布局严格遵循 `docs/布局要求.md` 的视觉规范。Mod 数据与 web 端共用同一个 Supabase 数据库。

## 架构设计

### 技术栈
- **UI 框架**: WinUI 3 + .NET 9.0
- **MVVM**: CommunityToolkit.Mvvm 8.4.0
- **数据源**: Supabase (PostgREST + Storage)
- **HTTP 客户端**: Supabase C# SDK
- **DI**: Microsoft.Extensions.Hosting
- **本地化**: WinUI3Localizer

### 文件清单

#### 新建文件 (7个)

| 文件 | 用途 |
|---|---|
| `Views/ModMarketPage.xaml` | Mod 市场页面 XAML 布局 |
| `Views/ModMarketPage.xaml.cs` | 代码后置 |
| `ViewModels/ModMarketViewModel.cs` | 页面 ViewModel |
| `Services/ModMarketService.cs` | Supabase 数据服务 |
| `Models/ModMarketMod.cs` | Mod 数据模型 |
| `Models/ModMarketCategory.cs` | 分类数据模型 |
| `Models/ModMarketOptions.cs` | Supabase 配置 Options 类 |

#### 修改文件 (6个)

| 文件 | 修改内容 |
|---|---|
| `App.xaml.cs` | 注册 ViewModel、Page、Service 到 DI；绑定 Supabase 配置 |
| `Services/PageService.cs` | 添加 ViewModel → Page 映射 |
| `Views/ShellPage.xaml` | 添加 "Mod 市场" 导航项 |
| `Strings/zh-cn/Resources.resw` | 添加中文本地化字符串 |
| `Strings/en-us/Resources.resw` | 添加英文本地化字符串 |
| `GIMI-ModManager.WinUI.csproj` | 添加 `Supabase` NuGet 包 |

## 实现步骤

### Step 1: 添加 Supabase NuGet 包

```xml
<PackageReference Include="Supabase" Version="1.1.1" />
```

### Step 2: 发现 Supabase Schema

使用 Supabase PostgREST API 获取 mods 表和分类表的字段结构。

### Step 3: 创建数据模型

- `ModMarketMod`: Id, Title, Description, AuthorName, AuthorAvatarUrl, PreviewImageUrl, CategoryId, Nsfw, LikesCount, DownloadsCount, CommentsCount, CreatedAt, Type, DownloadUrl
- `ModMarketCategory`: Id, Name, IconUrl, SortOrder
- `ModMarketOptions`: SupabaseUrl, SupabaseAnonKey

### Step 4: 创建 ModMarketService

封装 Supabase.Client，提供：
- `GetCategoriesAsync()` - 获取分类列表
- `GetModsAsync()` - 获取 mod 列表（支持分类、搜索、类型筛选、内容过滤、排序、分页）
- Supabase Storage 图片 URL 生成

### Step 5: 创建 ModMarketViewModel

- `ObservableCollection<ModMarketCategory> Categories` + `SelectedCategory`
- `ObservableCollection<ModMarketMod> Mods`
- 搜索框绑定 + 300ms 防抖
- 三个 ComboBox 过滤/排序
- 分页（Load More）
- 加载状态指示

### Step 6: 创建 ModMarketPage 布局

严格按照 `docs/布局要求.md`:

**左侧分类栏 (Width="240")**:
- "分类" 标题（18px Bold）
- ListView 分类列表（16px 图标 + 名称 + 数量）

**右侧主面板**:
- 工具栏 (Height="50"): 搜索框 + 3 个 ComboBox + 下载管理按钮
- 卡片网格 (ItemsRepeater + UniformGridLayout, 卡片宽度 ~220px)
- 每张卡片: 预览图(3:4) + NSFW 标签 + 标题(14px Bold 2行省略) + 作者行 + 统计行

**颜色**: 背景 #F5F7FA, 卡片 #FFFFFF, 圆角 8px

### Step 7: 注册导航和 DI

- `PageService.cs`: `Configure<ModMarketViewModel, ModMarketPage>()`
- `App.xaml.cs`: 注册 ModMarketService (Singleton), ModMarketViewModel (Transient), ModMarketPage (Transient)
- `ShellPage.xaml`: 添加 NavigationViewItem

### Step 8: 本地化

zh-cn 和 en-us 各添加 7 个字符串 key。

### Step 9: 配置 appsettings.json

```json
{
  "Supabase": {
    "Url": "https://xqwzgcxwdwpmkdbmzmve.supabase.co",
    "AnonKey": "sb_publishable_61QHtbRPJtHDCcwOZoynOA_mZlSNPfU"
  }
}
```

## 关键技术决策

| 决策 | 选择 | 原因 |
|---|---|---|
| 卡片布局 | ItemsRepeater + UniformGridLayout | 比 WrapPanel 性能更好，支持虚拟化 |
| Supabase 服务位置 | WinUI/Services/ | Supabase Client 是 UI 层关注点 |
| 搜索防抖 | CancellationTokenSource + 300ms | 与现有搜索模式一致 |
| 分页 | Supabase PostgREST Range() | 原生支持 |
| 图片加载 | Image 控件 + Supabase Storage public URL | 简单可靠 |

## 验证

1. `dotnet build` 编译通过
2. 导航到 "Mod 市场" 正常工作
3. 数据从 Supabase 正确加载
4. 搜索/过滤/排序功能正常
5. 分页功能正常
6. NSFW 标签正确显示
7. 中英文本地化正确
8. 窗口调整时卡片自动换行
