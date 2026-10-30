using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class RocketLearningCatalogTests
{
    [Fact]
    public void FindsOnlyRocketWithTheUpdatedDll()
    {
        using var root = TempTree.Create();
        var oldRocket = root.CreateRocket("old", dllBytes: 3L * 1024 * 1024);
        var newRocket = root.CreateRocket("new", dllBytes: 6L * 1024 * 1024);

        var found = RocketLearningCatalog.FindUpdatedRocket([root.Path], maxDepth: 3);

        Assert.Equal(newRocket, found);
        Assert.False(RocketLearningCatalog.IsUpdatedRocket(oldRocket));
        Assert.True(RocketLearningCatalog.IsUpdatedRocket(newRocket));
    }

    [Fact]
    public void ReturnsNullWhenNoUpdatedRocketExists()
    {
        using var root = TempTree.Create();
        root.CreateRocket("old", dllBytes: 1L * 1024 * 1024);

        Assert.Null(RocketLearningCatalog.FindUpdatedRocket([root.Path], maxDepth: 2));
    }

    [Fact]
    public void ReadsUhdReadyForTheRequestedRole()
    {
        var path = Path.Combine(Path.GetTempPath(), "learning-status-" + Guid.NewGuid().ToString("N") + ".tsv");
        File.WriteAllText(path,
            "# Rocket automatic learning v1\n" +
            "role\tdisplay\trecognizable\thd_ready\thd_pending\tuhd_ready\tuhd_pending\n" +
            "琳奈\t琳奈\t1\t414\t117\t142\t9\n" +
            "女漂\t女漂\t1\t149\t68\t191\t7\n", System.Text.Encoding.UTF8);

        try
        {
            var status = RocketLearningCatalog.ReadStatus("琳奈", path);

            Assert.NotNull(status);
            Assert.Equal(414, status.HdReady);
            Assert.Equal(142, status.UhdReady);
            Assert.Null(RocketLearningCatalog.ReadStatus("不存在", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TempTree : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("rocket-locate-").FullName;

        public string CreateRocket(string folder, long dllBytes)
        {
            var dir = System.IO.Path.Combine(Path, folder);
            Directory.CreateDirectory(dir);
            var exe = System.IO.Path.Combine(dir, "Rocket.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A]);

            var dll = System.IO.Path.Combine(dir, "backing", "Config", "WW", "Lod0_texture.dll");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dll)!);
            using var stream = File.Create(dll);
            stream.SetLength(dllBytes);

            return exe;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }

        public static TempTree Create() => new();
    }
}
