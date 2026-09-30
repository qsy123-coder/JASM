namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 解压结果的「包装层」剥离，以及「这个目录自己就是一个 Mod」的判定。
///
/// <para>
/// <b>为什么要剥包装层</b>：<see cref="GIMI_ModManager.Core.Services.DragAndDropScanner"/> 交出来的
/// <c>ExtractedFolder</c> 是 <c>%TEMP%\JASM_TMP\&lt;guid&gt;</c> —— 一个纯包装层，下面还压着
/// 「原文件名」目录、WinRAR SFX 常见的「文件名 + 同名目录」两层。安装向导的目录树从这层开始建，
/// 也就是用户要往下展开好几层才看得到真正的包内容。
/// </para>
///
/// <para>
/// <b>多合一包才是真麻烦</b>：向导认 mod 根靠 <c>AutoSetModRootFolder</c>，那是「在整棵树里
/// 递归找第一个 <c>mod.ini</c>」。对「根目录 + 若干变体子目录、根自己带 ini」这种包
/// （作者的说明与 Master ini 都摆在根上），猜出来的会是<b>包里第一个子目录</b> ——
/// 于是用户只装到包的一个碎片，而按键切换那些逻辑还全在没被装的 Master ini 里，
/// 表现就是「装是装上了，热键没反应 / 少东西」。
/// </para>
///
/// <para>
/// <see cref="LooksLikeSelfContainedModRoot"/> 用来识别这种情况：<b>根自己带 ini ⇒ 根就是 Mod</b>，
/// 整个包必须当一个 Mod 装。判定刻意保守（要求同时有 ini 和子目录），
/// 认不出来时调用方维持原有行为，不去动单 Mod 包那条走了很久的路。
/// </para>
/// </summary>
public static class ModPackageRootResolver
{
    /// <summary>
    /// 剥离包装层的层数上限。纯防御 —— 正常最多两层（原文件名目录 + SFX 的同名目录），
    /// 设个上限免得碰上畸形目录结构时一路挖下去。
    /// </summary>
    private const int MaxWrapperDepth = 8;

    /// <summary>
    /// 顺着「只有一个子目录」的链条往下走，返回真正的包内容根；没有可剥的层时原样返回入参。
    ///
    /// <para>
    /// 判据只看「是不是只有一个子目录」，不比对目录名 —— 比对名字会把
    /// 「载荷里就一层目录、但名字与包名无关」的情况漏掉。
    /// </para>
    /// </summary>
    public static DirectoryInfo ResolveContentRoot(DirectoryInfo extractedRoot)
    {
        ArgumentNullException.ThrowIfNull(extractedRoot);

        var current = extractedRoot;

        for (var depth = 0; depth < MaxWrapperDepth; depth++)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = current.GetFileSystemInfos();
            }
            catch (Exception)
            {
                // 读不动（权限 / 被占用）就别猜了：停在当前这层比抛出去好，后面还有原有的启发式兜底
                return current;
            }

            if (entries.Length != 1)
                return current;

            if (entries[0] is not DirectoryInfo onlySubFolder)
                return current;

            current = onlySubFolder;
        }

        return current;
    }

    /// <summary>
    /// 这个目录是否<b>自己就是一个 Mod</b>：顶层有 <c>*.ini</c>，<b>且</b>至少有一个子目录。
    ///
    /// <para>
    /// 两个条件都要：只有 ini 没有子目录 = 散落的 ini，多半不是 Mod；
    /// 只有子目录没有 ini = 典型的「一堆互不相干的 Mod 打成一个包」，那种情况不该由这里拍板。
    /// </para>
    ///
    /// <para>
    /// ini 认<b>任意名字</b>而不是只看 <c>mod.ini</c>：社区包里的根 ini 常被作者起成
    /// <c>Master_xxx.ini</c> 这种名字（子目录里那些才是自动生成的 <c>mod.ini</c>）。
    /// </para>
    /// </summary>
    public static bool LooksLikeSelfContainedModRoot(DirectoryInfo contentRoot)
    {
        ArgumentNullException.ThrowIfNull(contentRoot);

        try
        {
            var entries = contentRoot.GetFileSystemInfos();

            var hasIni = entries.Any(entry =>
                entry is FileInfo file && file.Extension.Equals(".ini", StringComparison.OrdinalIgnoreCase));

            var hasSubFolder = entries.Any(entry => entry is DirectoryInfo);

            return hasIni && hasSubFolder;
        }
        catch (Exception)
        {
            // 读不动就别硬判成「是」——误判会让用户丢掉在向导里换 mod 根的机会
            return false;
        }
    }
}