using System.Text.Json;
using Serilog;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 一条安装记录：从商店装过的**一个 mod**（不是一次安装动作）。
///
/// 字段全是「事后还要用得上」的：<see cref="ModId"/> / <see cref="FileId"/> / <see cref="Md5"/>
/// 是「有没有新版」的比对依据，<see cref="FolderPath"/> 是「它还在不在」的依据，
/// <see cref="Character"/> 与 <see cref="ModPageUrl"/> 是给人排错用的（装到哪个角色下、
/// 当初是从哪个页面装的）。
/// </summary>
/// <param name="ModId">GameBanana 的 mod id（索引的主键，按它去重）。</param>
/// <param name="FileId">装的是哪个文件。同一 mod 换了文件就是「有新版」。</param>
/// <param name="Md5">装的那份文件的 md5；上游没给则为 null。</param>
/// <param name="Version">版本号（文件级缺失时是 mod 级）；上游没给则为 null。</param>
/// <param name="Character">装到的本地角色内部名。</param>
/// <param name="ModPageUrl">mod 页面地址。</param>
/// <param name="FolderPath">装好的 mod 目录（绝对路径）。</param>
/// <param name="LocalModId">
/// 装出来那个 mod 的本地 id（JASM 自己发的 Guid，与「模组更新」那条路给安装向导的是同一个）。
/// 再装一次时靠它让向导**就地更新**而不是在同一角色下多塞一份；读不出来时为 null
/// （那时向导会退化成「新增一个」，不会出错）。
/// </param>
/// <param name="InstalledAt">写入这条记录的时间。</param>
public sealed record ModStoreInstallRecord(
    string ModId,
    string FileId,
    string? Md5,
    string? Version,
    string? Character,
    string? ModPageUrl,
    string FolderPath,
    Guid? LocalModId,
    DateTimeOffset InstalledAt);

/// <summary>落盘形状。包一层而不是直接存数组：将来加字段 / 换结构时有地方放版本号。</summary>
internal sealed class ModStoreInstallIndexFile
{
    public int SchemaVersion { get; set; } = ModStoreInstallIndex.CurrentSchemaVersion;

    public List<ModStoreInstallRecord> Records { get; set; } = [];
}

/// <summary>
/// 本地安装索引（PRD 第 8 项）：一份 JSON，记着「从商店装过哪些 mod」。
///
/// 三件事决定了它的形状：
/// <list type="number">
///   <item><b>不依赖任何服务端</b>（PRD 的硬要求）：判定「已装 / 可更新」只用这份本地文件 +
///         磁盘上的目录，换个网络环境、GameBanana 挂了都不影响。</item>
///   <item><b>读要同步、写才异步</b>：读发生在界面刷卡片的时候（一页十几张卡片各问一次），
///         同步查内存字典最省事；写只发生在一次安装成功之后，走文件 I/O 但不必让人等，
///         所以做成 <see cref="UpsertAsync"/>。构造函数里把文件读进内存，之后读就都不碰磁盘。</item>
///   <item><b>坏了当空</b>：文件读不出来（截断 / 手工改坏）只记一条 Warning 并当作「什么都没装过」，
///         **不删文件** —— 商店照常能浏览、能安装，用户想找回线索时文件还在。</item>
/// </list>
/// </summary>
public sealed class ModStoreInstallIndex
{
    /// <summary>当前写出的结构版本。读进来的版本更高（降级运行）时不报错，能读多少读多少。</summary>
    internal const int CurrentSchemaVersion = 1;

    private const string TempFileSuffix = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private readonly ILogger _logger;
    private readonly string _indexPath;

    /// <summary>写盘的闸门：一条记录一条记录地写，避免两次写把临时文件搅在一起。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>保护 <see cref="_records"/>：读在界面线程、写在安装完成的续体上，两边都可能碰它。</summary>
    private readonly object _gate = new();

    private readonly Dictionary<string, ModStoreInstallRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public ModStoreInstallIndex(ILogger logger, string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        _logger = logger?.ForContext<ModStoreInstallIndex>() ?? throw new ArgumentNullException(nameof(logger));
        _indexPath = indexPath;

        Load();
    }

    /// <summary>全部记录（快照；界面侧拿去算角标用）。</summary>
    public IReadOnlyList<ModStoreInstallRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.Values.ToArray();
            }
        }
    }

    /// <summary>这个 mod 的原始记录；没装过返回 null。</summary>
    public ModStoreInstallRecord? Find(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return null;

        lock (_gate)
        {
            return _records.GetValueOrDefault(modId);
        }
    }

    /// <summary>
    /// 「现在还装着吗」= 有记录 **且** 那个目录还在。
    ///
    /// 双重判定是 PRD 的验收项：用户自己把 mod 目录删了，角标不能再显示「已安装」——
    /// 而索引里那条记录还留着（我们没理由去猜他是删了还是只是挪了个位置）。
    /// </summary>
    public bool IsInstalled(string? modId)
    {
        var record = Find(modId);

        return record is not null && !string.IsNullOrWhiteSpace(record.FolderPath)
                                && Directory.Exists(record.FolderPath);
    }

    /// <summary>
    /// 写入 / 覆盖一条记录（同一个 mod 只留一条：装新版就是覆盖旧记录，不是加一条）。
    ///
    /// **不向上抛**：写失败只记 Error —— 记录是「让界面更聪明」的锦上添花，
    /// 装已经装完了，不该为了一条记录把安装成功变成失败。
    /// </summary>
    public async Task UpsertAsync(ModStoreInstallRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.ModId))
            throw new ArgumentException("安装记录必须有 mod id", nameof(record));

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ModStoreInstallRecord> snapshot;
            lock (_gate)
            {
                _records[record.ModId] = record;
                snapshot = _records.Values.ToList();
            }

            await WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 删掉一条记录。用于「记录还在、目录已经没了」的自愈 —— 否则文件会一直堆着死记录。
    /// 与 <see cref="UpsertAsync"/> 一样不向上抛。
    /// </summary>
    public async Task RemoveAsync(string modId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ModStoreInstallRecord> snapshot;
            lock (_gate)
            {
                if (!_records.Remove(modId))
                    return;

                snapshot = _records.Values.ToList();
            }

            await WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ─── 磁盘 ──────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath))
                return;

            var file = JsonSerializer.Deserialize<ModStoreInstallIndexFile>(File.ReadAllText(_indexPath),
                JsonOptions);

            if (file?.Records is null)
                return;

            foreach (var record in file.Records)
            {
                // 文件可能被手工改过：没有 mod id 的那条认不出是哪个 mod，丢掉比留个谜好。
                if (!string.IsNullOrWhiteSpace(record.ModId))
                    _records[record.ModId] = record;
            }

            _logger.Debug("Store install index loaded: {Count} records", _records.Count);
        }
        catch (Exception ex)
        {
            // 只记文件名不记全路径（项目约定：日志里不带用户的本地路径细节）。
            _logger.Warning(ex, "Could not read the store install index {File}, starting empty",
                Path.GetFileName(_indexPath));
            _records.Clear();
        }
    }

    private async Task WriteAsync(List<ModStoreInstallRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new ModStoreInstallIndexFile { SchemaVersion = CurrentSchemaVersion, Records = records },
                JsonOptions);

            var directory = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // 先写临时文件再 Move(overwrite)：直接覆盖的话，写到一半断电 / 被杀就会留下半截 JSON，
            // 而半截 JSON 下次启动是读不出来的 —— 那等于「记录全丢」，比晚写几十毫秒糟得多。
            var tempPath = _indexPath + TempFileSuffix;
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _indexPath, overwrite: true);

            _logger.Debug("Store install index saved: {Count} records", records.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not write the store install index {File}", Path.GetFileName(_indexPath));
        }
    }
}