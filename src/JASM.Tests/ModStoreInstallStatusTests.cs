using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;

namespace JASM.Tests;

/// <summary>
/// 商店「已装 / 可更新」判定（PRD Story 4）。这里锁的是**别误报**：
/// 目录被删了不能说已装、作者重压一遍不能说可更新，而用户给 mod 改过名时不能说没装 ——
/// 这一项的 KPI 就是误报率，误报全部出在这些边界上。
/// </summary>
public sealed class ModStoreInstallStatusTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(),
        "jasm-install-status-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响断言，交给系统。
        }
    }

    /// <summary>建一个真实存在的目录当「装好的 mod 目录」—— 判定要查它。</summary>
    private string CreateFolder()
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ModStoreInstallRecord Record(string fileId = "100", string? md5 = "abc",
        string? folderPath = null, Guid? localModId = null) =>
        new("709792", fileId, md5, "1.0", "Qingxiao", "https://gamebanana.com/mods/709792",
            folderPath ?? "C:/nope", localModId, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// 造一条文件记录。<paramref name="dateAdded"/> 传 0 = 上游没给时间戳
    /// （0 会被 <c>ModStoreFile</c> 当成「没有」，与真实数据的口径一致）。
    /// </summary>
    private static ModStoreFile File(int fileId, string? md5, long dateAdded, bool archived = false) =>
        ModStoreFile.TryCreate(new ApiModFileInfo
        {
            FileId = fileId,
            FileName = $"mod-{fileId}.zip",
            Md5Checksum = md5!,
            DateAdded = dateAdded
        }, modVersion: "1.0", fromArchivedList: archived)!;

    // ─── 已装判定 ──────────────────────────────────────────────

    [Fact]
    public void IsInstalled_IsFalseWithoutARecord()
    {
        // 没记录 = 没从商店装过。这里连目录都不用看。
        Assert.False(ModStoreInstallStatus.IsInstalled(null));
    }

    [Fact]
    public void IsInstalled_IsTrueWhenTheRecordedFolderIsThere()
    {
        Assert.True(ModStoreInstallStatus.IsInstalled(Record(folderPath: CreateFolder())));
    }

    [Fact]
    public void IsInstalled_IsFalseWhenTheRecordedFolderIsGone()
    {
        var folder = CreateFolder();
        Directory.Delete(folder, recursive: true);

        Assert.False(ModStoreInstallStatus.IsInstalled(Record(folderPath: folder)));
    }

    [Fact]
    public void IsInstalled_IsFalseAfterTheFolderIsDeletedButTheRecordItselfStays()
    {
        var folder = CreateFolder();
        var record = Record(folderPath: folder);

        Assert.True(ModStoreInstallStatus.IsInstalled(record));

        // 用户自己把 mod 删了：角标不能再显示「已安装」，但记录不该被当成错的 ——
        // 我们没理由猜他是删了还是挪走了准备再挪回来（判定只看目录，记录由索引留着）。
        Directory.Delete(folder, recursive: true);

        Assert.False(ModStoreInstallStatus.IsInstalled(record));
    }

    [Fact]
    public void IsInstalled_IsTrueWhenTheModWasMovedButIsStillTracked()
    {
        var movedTo = CreateFolder();

        // 记录里那条路径早就过期了（用户在 JASM 里给 mod 改了名 / 挪了位置）——
        // 只认它会把「明明还装着」说成没装，所以路径要问本地 mod 列表。
        var record = Record(folderPath: "C:/stale/path", localModId: Guid.NewGuid());

        Assert.True(ModStoreInstallStatus.IsInstalled(record, _ => movedTo));
    }

    [Fact]
    public void IsInstalled_IsFalseWhenTheTrackedModIsGoneFromDisk()
    {
        var record = Record(folderPath: CreateFolder(), localModId: Guid.NewGuid());
        var gonePath = Path.Combine(_folder, "gone");

        // JASM 那边还认着这个 mod、但它的目录已经不在了（用户在资源管理器里删的）：
        // 以 mod 列表给的路径为准 → 未装。这里如果退回记录里的路径就会误报「已安装」。
        Assert.False(ModStoreInstallStatus.IsInstalled(record, _ => gonePath));
    }

    [Fact]
    public void IsInstalled_FallsBackToTheRecordedFolderWhenTheModIsNotTracked()
    {
        var folder = CreateFolder();

        // 本地 mod 列表已经不认这个 id 了（JASM 还没刷新到 / 用户挪到了别的角色下）：
        // 退回记录里的路径，看那个目录还在不在。
        Assert.True(ModStoreInstallStatus.IsInstalled(Record(folderPath: folder, localModId: Guid.NewGuid()),
            _ => null));
    }

    // ─── 可更新判定 ────────────────────────────────────────────

    [Fact]
    public void HasUpdate_IsFalseWithoutARecordOrWithoutFiles()
    {
        var files = new[] { File(200, "xyz", 200) };

        Assert.False(ModStoreInstallStatus.HasUpdate(null, files));
        Assert.False(ModStoreInstallStatus.HasUpdate(Record(), null));
        Assert.False(ModStoreInstallStatus.HasUpdate(Record(), []));
    }

    [Fact]
    public void HasUpdate_IsFalseWhenTheLatestFileIsTheOneThatWasInstalled()
    {
        // 装的是 100，而 100 就是最新的那个（200 是更早的一份）→ 没有更新。
        var files = new[] { File(100, "abc", 200), File(90, "old", 100) };

        Assert.False(ModStoreInstallStatus.HasUpdate(Record(fileId: "100", md5: "abc"), files));
    }

    [Fact]
    public void HasUpdate_IsTrueWhenTheLatestFileHasDifferentContent()
    {
        var files = new[] { File(200, "xyz", 200), File(100, "abc", 100) };

        Assert.True(ModStoreInstallStatus.HasUpdate(Record(fileId: "100", md5: "abc"), files));
    }

    [Fact]
    public void HasUpdate_IgnoresAReUploadOfTheSameContent()
    {
        // 作者重压了一遍：file id 变了（200），md5 没变 → 装出来的东西一模一样，不该报「可更新」。
        var files = new[] { File(200, "abc", 200) };

        Assert.False(ModStoreInstallStatus.HasUpdate(Record(fileId: "100", md5: "abc"), files));
    }

    [Fact]
    public void HasUpdate_ComparesFileIdsWhenAChecksumIsMissing()
    {
        var files = new[] { File(200, null, 200) };

        // 两边都没有 md5（上游不给）→ 退回比 file id：一样就说没更新，不一样才报。
        Assert.False(ModStoreInstallStatus.HasUpdate(Record(fileId: "200", md5: null), files));
        Assert.True(ModStoreInstallStatus.HasUpdate(Record(fileId: "100", md5: null), files));
    }

    [Fact]
    public void HasUpdate_UsesTheArchivedFileWhenNothingIsActive()
    {
        // 被隐藏 / 归档的 mod 文件全在归档那份里（实测 709792）—— 那时它就是唯一的「最新」。
        var files = new[] { File(200, "xyz", 200, archived: true) };

        Assert.True(ModStoreInstallStatus.HasUpdate(Record(fileId: "100", md5: "abc"), files));
    }

    // ─── 「最新那个文件」的选择 ────────────────────────────────

    [Fact]
    public void FindLatestFile_ReturnsNullWithoutFiles()
    {
        Assert.Null(ModStoreInstallStatus.FindLatestFile(null));
        Assert.Null(ModStoreInstallStatus.FindLatestFile([]));
    }

    [Fact]
    public void FindLatestFile_PicksTheNewestActiveFile()
    {
        var files = new[] { File(100, "a", 100), File(200, "b", 300), File(150, "c", 200) };

        Assert.Equal("200", ModStoreInstallStatus.FindLatestFile(files)!.FileId.ToString());
    }

    [Fact]
    public void FindLatestFile_FallsBackToTheFirstActiveFileWithoutTimestamps()
    {
        // 文件都没有时间戳（上游键缺失）：用清单顺序，也就是作者自己排的「该装的那个」。
        var files = new[] { File(100, "a", 0), File(200, "b", 0) };

        Assert.Equal("100", ModStoreInstallStatus.FindLatestFile(files)!.FileId.ToString());
    }

    [Fact]
    public void FindLatestFile_PrefersActiveOverANewerArchivedFile()
    {
        // 归档文件**更新**（时间戳更大）也不该被选成「最新」：归档意味着作者已经撤下它。
        var files = new[] { File(100, "a", 100), File(200, "b", 999, archived: true) };

        Assert.Equal("100", ModStoreInstallStatus.FindLatestFile(files)!.FileId.ToString());
    }
}