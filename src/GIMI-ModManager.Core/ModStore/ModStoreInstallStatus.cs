namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 「这个 mod 现在还装着吗 / 有没有新版」的判定（PRD Story 4 的两个角标）。
///
/// 为什么是**静态纯函数**：两条判定只依赖「本地安装记录」+「页面上已经拿到的数据」，
/// 不碰网络、不碰界面 —— 摆在这里就能被单测钉住。这一项的 KPI 是「已装 / 可更新误报 ≤ 1%」，
/// 而误报全部来自判定的边界（目录被删、用户改过名、作者重压了一遍、文件级版本缺失…），
/// 那些边界只靠手点是验不完的。
///
/// 环境相关的那一半（本地那份 mod 现在在哪、文件清单长什么样）由调用方传进来 ——
/// 这个类不知道 JASM 的 mod 列表，也不需要知道。
/// </summary>
public static class ModStoreInstallStatus
{
    /// <summary>
    /// 已装判定 = 有记录 **且** 那份 mod 的目录现在还在（PRD 明确要求的双重判定）。
    ///
    /// 目录路径优先问 <paramref name="resolveCurrentModPath"/>（本地 mod 列表里**现在**认的路径）：
    /// 用户完全可能在 JASM 里给 mod 改过名、或者挪过位置，那时记录里存的那条路径已经过期 ——
    /// 只认它会把「明明还装着」说成没装。委托给不出路径（mod 已经被删、或者 JASM 还没刷新到它）
    /// 时才退回记录里的路径，看那个目录还在不在。
    ///
    /// 用户手动删掉那个 mod 目录 → 两条路都说不存在 → 判定为**未装**（角标不能残留）。
    /// 但记录本身**不删**：我们没理由去猜他是删了、还是挪走了准备再挪回来。
    /// </summary>
    public static bool IsInstalled(ModStoreInstallRecord? record, Func<Guid, string?>? resolveCurrentModPath = null)
    {
        if (record is null)
            return false;

        if (record.LocalModId is { } localModId &&
            resolveCurrentModPath?.Invoke(localModId) is { } currentPath)
        {
            return Directory.Exists(currentPath);
        }

        return !string.IsNullOrWhiteSpace(record.FolderPath) && Directory.Exists(record.FolderPath);
    }

    /// <summary>
    /// 可更新判定 = 这个 mod 现在最新那个文件，跟记录里装的那份**不是同一份内容**。
    ///
    /// 比对的是**内容**而不是版本号字符串：md5（内容指纹）两边都有时以 md5 为准 ——
    /// 作者重压一遍（内容一模一样、file id 变了）不该报「可更新」，那正是「装出来的东西相同」的意思
    /// （与归档缓存用 md5 认同一个文件的道理一致）；md5 缺一边时才退回比 file id。
    ///
    /// 版本号字符串**不参与判定**：上游的文件级版本经常整个键都不存在，格式也没统一
    /// （"1.0" / "v1.0" / "1.0.0"），拿它比只会造出误报。
    /// </summary>
    public static bool HasUpdate(ModStoreInstallRecord? record, IReadOnlyList<ModStoreFile>? files)
    {
        if (record is null || FindLatestFile(files) is not { } latest)
            return false;

        if (!string.IsNullOrWhiteSpace(record.Md5) && !string.IsNullOrWhiteSpace(latest.Md5Checksum))
            return !string.Equals(record.Md5, latest.Md5Checksum, StringComparison.OrdinalIgnoreCase);

        return !string.Equals(record.FileId, latest.FileId.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 「最新的文件」= 活跃文件里 <see cref="ModStoreFile.DateAdded"/> 最大的那个。
    /// 活跃文件全都没有时间戳时退回**清单里的第一个活跃文件** —— 那个顺序是作者自己排的（实测），
    /// 也就是作者认为该装的那个。活跃文件一个都没有（全归档）时才去归档里找。
    ///
    /// ⚠️ 已知偏差：同一个 mod 的多形态文件（「有图版」/「无图版」）由作者按不同日期上传时，
    /// 「最新」可能是另一个形态，装的是那个旧形态时会被判成「可更新」。宁可这样也不反过来猜：
    /// 若改成「记录里那一份还是不是清单里的最新」，作者换文件 id 重传时就会漏掉真正的更新 ——
    /// 漏报更新比多报一次更新糟（前者用户永远不会知道，后者只是多点一下、装到的东西仍然更新）。
    /// </summary>
    public static ModStoreFile? FindLatestFile(IReadOnlyList<ModStoreFile>? files)
    {
        if (files is null || files.Count == 0)
            return null;

        var candidates = files.Where(file => !file.IsArchived).ToArray();
        if (candidates.Length == 0)
            candidates = files.ToArray();

        var dated = candidates.Where(file => file.DateAdded is not null).ToArray();

        return dated.Length > 0 ? dated.MaxBy(file => file.DateAdded!.Value) : candidates[0];
    }
}