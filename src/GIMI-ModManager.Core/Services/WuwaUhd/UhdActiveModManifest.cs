using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// JASM → 原生映射 DLL 的「当前角色 / 当前已启用 mod」清单，落到
/// <c>%LOCALAPPDATA%\JASM\UhdRuntime\active-mod.json</c>。
///
/// 原生 DLL 在游戏进程里读这份文件，得到「mod 的 HD hash → mod 自带 DDS 路径」，
/// 用于把 UHD 档位下游戏绑定的贴图重定向到 mod 的贴图资源。
///
/// **JSON 字段名是跨语言契约**：原生侧手写解析（不引 Newtonsoft），改名字/改层级
/// 必须同步 <c>src/Jasm.UhdMapper</c>，否则 DLL 会静默读不到映射。
/// </summary>
public sealed class UhdActiveModManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string WuwaGameId = "WuWa";

    /// <summary>契约版本。DLL 只认自己支持的版本，其它版本应忽略整份清单。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }

    [JsonPropertyName("game")]
    public string Game { get; init; } = WuwaGameId;

    /// <summary>角色显示名（与 Rocket 学习库里的中文名一致）。</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>角色内部名（JASM 的角色标识，便于排错时对回模组目录）。</summary>
    [JsonPropertyName("roleInternalName")]
    public string RoleInternalName { get; init; } = string.Empty;

    [JsonPropertyName("mods")]
    public IReadOnlyList<UhdActiveModEntry> Mods { get; init; } = [];
}

/// <summary>一个已启用的 mod：根目录、参与的 ini，以及解析出的贴图绑定。</summary>
public sealed class UhdActiveModEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("rootPath")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("iniFiles")]
    public IReadOnlyList<string> IniFiles { get; init; } = [];

    [JsonPropertyName("textures")]
    public IReadOnlyList<UhdActiveModTexture> Textures { get; init; } = [];
}

/// <summary>
/// 一条贴图绑定：mod 用 <see cref="HdHash"/> 绑定的资源，DLL 在 UHD 档位下
/// 需要把它和游戏侧观察到的 UHD hash 配对，再用 <see cref="DdsPath"/> 的贴图替换。
/// </summary>
public sealed class UhdActiveModTexture
{
    /// <summary>mod ini 里 <c>hash =</c> 的值（8 位小写十六进制）。</summary>
    [JsonPropertyName("hdHash")]
    public string HdHash { get; init; } = string.Empty;

    /// <summary><c>this = ResourceXxx</c> 指向的资源段名；解析不到时为 null。</summary>
    [JsonPropertyName("resourceSection")]
    public string? ResourceSection { get; init; }

    /// <summary>资源段 <c>filename =</c> 解析出的绝对 DDS 路径；解析不到时为 null。</summary>
    [JsonPropertyName("ddsPath")]
    public string? DdsPath { get; init; }

    /// <summary>定义该绑定的 <c>[TextureOverride...]</c> 段名。</summary>
    [JsonPropertyName("overrideSection")]
    public string OverrideSection { get; init; } = string.Empty;

    /// <summary>可选的已知 UHD 配对（Rocket 学习结果或已生成的 _UHD_ 段）。DLL 可据此跳过学习。</summary>
    [JsonPropertyName("uhdHashes")]
    public IReadOnlyList<string> UhdHashes { get; init; } = [];

    /// <summary>这个绑定来自哪个 ini 文件（绝对路径），排错用。</summary>
    [JsonPropertyName("iniFile")]
    public string? IniFile { get; init; }
}

/// <summary><see cref="UhdActiveModStore"/> 的 JSON 读写设置（与 DLL 的约定见类注释）。</summary>
public static class UhdActiveModJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        // 本地诊断文件：保留中文可读性，别全转成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(UhdActiveModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    /// <summary>解析失败（畸形 JSON / 类型不匹配）返回 null，绝不抛异常。</summary>
    public static UhdActiveModManifest? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<UhdActiveModManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}