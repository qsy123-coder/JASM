using GIMI_ModManager.Core.ModStore;
using Serilog;

namespace JASM.Tests;

/// <summary>
/// 商店的本地安装索引。这里锁的是「已装判定不该说谎」这条底线：
/// 没记录要说没装、目录没了也要说没装，而文件被改坏时**不能**把浏览与安装一起带崩。
/// </summary>
public sealed class ModStoreInstallIndexTests : IDisposable
{
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    private readonly string _folder = Path.Combine(Path.GetTempPath(),
        "jasm-install-index-" + Guid.NewGuid().ToString("N"));

    private string IndexPath => Path.Combine(_folder, "ModStoreInstalls.json");

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

    private ModStoreInstallIndex CreateIndex() => new(SilentLogger, IndexPath);

    /// <summary>建一个真实存在的目录当「装好的 mod 目录」—— 已装判定要查它。</summary>
    private string CreateInstalledFolder()
    {
        var path = Path.Combine(_folder, "mods", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ModStoreInstallRecord Record(string modId, string folderPath,
        string fileId = "100", string? md5 = "abc", string? version = "1.0") =>
        new(modId, fileId, md5, version, "Qingxiao", $"https://gamebanana.com/mods/{modId}",
            folderPath, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Upsert_IsReadBackByAFreshInstance()
    {
        var folder = CreateInstalledFolder();

        await CreateIndex().UpsertAsync(Record("709792", folder));

        var record = CreateIndex().Find("709792");

        Assert.NotNull(record);
        Assert.Equal("100", record.FileId);
        Assert.Equal("abc", record.Md5);
        Assert.Equal("1.0", record.Version);
        Assert.Equal("Qingxiao", record.Character);
        Assert.Equal("https://gamebanana.com/mods/709792", record.ModPageUrl);
        Assert.Equal(folder, record.FolderPath);
        Assert.Equal(DateTimeOffset.UnixEpoch, record.InstalledAt);
    }

    [Fact]
    public async Task Upsert_ReplacesTheRecordOfTheSameModInsteadOfAddingOne()
    {
        var first = CreateInstalledFolder();
        var second = CreateInstalledFolder();
        var index = CreateIndex();

        await index.UpsertAsync(Record("709792", first, fileId: "100", version: "1.0"));
        await index.UpsertAsync(Record("709792", second, fileId: "200", version: "2.0"));

        Assert.Single(index.Records);
        Assert.Equal("200", index.Find("709792")!.FileId);

        // 落盘的那份也必须是覆盖后的，不然下次启动读回来的是旧记录。
        Assert.Equal(second, CreateIndex().Find("709792")!.FolderPath);
    }

    [Fact]
    public void Find_ReturnsNullWhenNothingWasInstalled()
    {
        Assert.Null(CreateIndex().Find("709792"));
        Assert.False(CreateIndex().IsInstalled("709792"));
    }

    [Fact]
    public async Task IsInstalled_TurnsFalseAfterTheFolderIsDeleted()
    {
        var folder = CreateInstalledFolder();
        var index = CreateIndex();
        await index.UpsertAsync(Record("709792", folder));

        Assert.True(index.IsInstalled("709792"));

        // 用户自己把这个 mod 删了：记录还在（我们没理由猜他是删了还是挪了），但角标不能再说「已安装」。
        Directory.Delete(folder, recursive: true);

        Assert.False(index.IsInstalled("709792"));
        Assert.NotNull(index.Find("709792"));
    }

    [Fact]
    public async Task Remove_DropsTheRecordAndPersists()
    {
        var index = CreateIndex();
        await index.UpsertAsync(Record("1", CreateInstalledFolder()));
        await index.UpsertAsync(Record("2", CreateInstalledFolder()));

        await index.RemoveAsync("1");

        Assert.Null(index.Find("1"));
        Assert.NotNull(index.Find("2"));
        Assert.Null(CreateIndex().Find("1"));
    }

    [Fact]
    public async Task Constructor_TreatsACorruptFileAsAnEmptyIndex()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(IndexPath, "{ 这不是 JSON");

        // 能构造出来 = 没抛。坏文件只该影响「已装」判定，不该把商店的浏览与安装一起带崩。
        var index = CreateIndex();

        Assert.Empty(index.Records);
        Assert.Null(index.Find("709792"));

        // 而且还能正常写入：下一次写就把坏文件换掉了。
        await index.UpsertAsync(Record("709792", CreateInstalledFolder()));

        Assert.NotNull(CreateIndex().Find("709792"));
    }

    [Fact]
    public void Constructor_IsEmptyWhenTheFileDoesNotExist()
    {
        Assert.False(File.Exists(IndexPath));
        Assert.Empty(CreateIndex().Records);
    }

    [Fact]
    public async Task Upsert_RejectsARecordWithoutAModId()
    {
        var index = CreateIndex();

        // 空 id 直接拒绝：留着它只会让「这条到底对应哪个 mod」永远说不清。
        await Assert.ThrowsAsync<ArgumentException>(() => index.UpsertAsync(Record("", CreateInstalledFolder())));

        Assert.Empty(index.Records);
    }

    [Fact]
    public async Task Upsert_LeavesNoTempFileBehind()
    {
        await CreateIndex().UpsertAsync(Record("709792", CreateInstalledFolder()));

        Assert.DoesNotContain(Directory.GetFiles(_folder), file => file.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Upsert_KeepsEveryRecordUnderConcurrentWrites()
    {
        var index = CreateIndex();

        // 写盘是串行的；同时丢十条进去，最后必须一条不少 —— 快照取自内存字典，不是各自手里的那份。
        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => index.UpsertAsync(Record(i.ToString(), CreateInstalledFolder()))));

        Assert.Equal(10, index.Records.Count);
        Assert.Equal(10, CreateIndex().Records.Count);
    }
}