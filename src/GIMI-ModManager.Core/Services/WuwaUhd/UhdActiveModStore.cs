using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// <c>active-mod.json</c> 的落盘/读取。位置固定在
/// <c>%LOCALAPPDATA%\JASM\UhdRuntime\</c> —— 原生 DLL 与提权助手都按这个路径找，
/// 不是用户可配置项。
///
/// 写入是「临时文件 + 原子替换」：DLL 可能在游戏里同时监听/读取这个文件，
/// 直接覆盖会读到半截 JSON。
/// </summary>
public static class UhdActiveModStore
{
    public const string ActiveModFileName = "active-mod.json";
    public const string RuntimeDirectoryName = "UhdRuntime";

    public static string RuntimeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JASM", RuntimeDirectoryName);

    public static string ActiveModPath => Path.Combine(RuntimeDirectory, ActiveModFileName);

    /// <summary>序列化并原子写入；返回写入的完整路径。</summary>
    public static string Write(UhdActiveModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(RuntimeDirectory);

        var json = UhdActiveModJson.Serialize(manifest);
        var temp = ActiveModPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, ActiveModPath, overwrite: true);

        return ActiveModPath;
    }

    /// <summary>读回清单（排错/状态展示用）。文件不存在或 JSON 畸形返回 null。</summary>
    public static UhdActiveModManifest? TryRead()
    {
        if (!File.Exists(ActiveModPath))
            return null;

        try
        {
            return UhdActiveModJson.Deserialize(File.ReadAllText(ActiveModPath, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 删除清单（关闭注入启动 / 切换非鸣潮游戏时调用）：DLL 读不到文件就会禁用映射，
    /// 不会继续用上一个角色的旧映射。
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(ActiveModPath))
                File.Delete(ActiveModPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 删不掉也不能影响启动流程；DLL 会按 schemaVersion/generatedAtUtc 判断过期
        }
    }
}