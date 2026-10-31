using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// 原生映射 DLL 的运行配置（<c>%LOCALAPPDATA%\JASM\UhdRuntime\mapper.ini</c>）与观察文件。
///
/// <para>
/// 自研学习（M3）靠 DLL 的 <c>learn=1</c>：只记录每帧的「draw 身份 + 槽位贴图 hash」，不替换任何贴图。
/// HD、UHD 两档位各跑一次后由 <see cref="UhdLearningStore.JoinFiles"/> 配对。
/// </para>
/// </summary>
public static class UhdMapperConfig
{
    public const string MapperIniFileName = "mapper.ini";
    public const string LearnFileName = "learn.tsv";
    public const string HdLearnFileName = "learn-hd.tsv";
    public const string UhdLearnFileName = "learn-uhd.tsv";

    public static string MapperIniPath => Path.Combine(UhdActiveModStore.RuntimeDirectory, MapperIniFileName);

    public static string LearnFilePath => Path.Combine(UhdActiveModStore.RuntimeDirectory, LearnFileName);

    public static string HdLearnPath => Path.Combine(UhdActiveModStore.RuntimeDirectory, HdLearnFileName);

    public static string UhdLearnPath => Path.Combine(UhdActiveModStore.RuntimeDirectory, UhdLearnFileName);

    /// <summary>
    /// 写 <c>mapper.ini</c>：<paramref name="enabled"/> 时开 learn、性能与替换全部关闭；
    /// 关闭后回到安全的 instrument。写失败抛 <see cref="IOException"/>，由调用方决定怎么提示。
    /// </summary>
    public static void SetLearnMode(bool enabled)
    {
        Directory.CreateDirectory(UhdActiveModStore.RuntimeDirectory);

        var text = enabled
            ? $"mode=instrument{Environment.NewLine}learn=1{Environment.NewLine}log=info{Environment.NewLine}"
            : $"mode=instrument{Environment.NewLine}log=info{Environment.NewLine}";

        var temp = MapperIniPath + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, MapperIniPath, overwrite: true);
    }

    /// <summary>把当前 <c>learn.tsv</c> 挪到指定文件（存在才动）；返回是否成功。</summary>
    public static bool ArchiveLearnFile(string destination)
    {
        try
        {
            if (!File.Exists(LearnFilePath))
                return false;

            if (File.Exists(destination))
                File.Delete(destination);

            File.Move(LearnFilePath, destination);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void ClearLearnFile()
    {
        try
        {
            if (File.Exists(LearnFilePath))
                File.Delete(LearnFilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 删不掉也不影响下一轮：DLL 会用新文件覆盖写入
        }
    }

    /// <summary>当前观察文件里的采样行数（不含表头），用于向导显示进度。</summary>
    public static int CountLearnSamples()
    {
        try
        {
            if (!File.Exists(LearnFilePath))
                return 0;

            var count = 0;
            foreach (var line in File.ReadLines(LearnFilePath, Encoding.UTF8))
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                    count++;
            }

            return count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}