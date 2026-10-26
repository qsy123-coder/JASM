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
    /// 拖拽安装的落盘根：从 <c>JASM_TMP\&lt;guid&gt;</c> 一路剥到「自己带 ini 的那一层」，
    /// 再把它改名叫包名（<c>&lt;guid&gt;</c> 下面那一层，已由 Scanner 去掉容器后缀）。
    ///
    /// <para>
    /// <b>为什么要剥到 ini 那一层</b>：多一层目录，XXMI 就扫不到 mod 根上的 ini —— 实机表现是
    /// <b>启动直接崩溃</b>（用户实测：<c>DISABLED_xxx.exe\xxx\PebbleRox-…\single_layer_depth.ini</c>
    /// 这样三层，把嵌套去掉就正常）。SFX 装出来的包常常是「包名\包名\真内容」这种形状，
    /// 只剥 <c>&lt;guid&gt;</c> 一层不够。
    /// </para>
    ///
    /// <para>
    /// <b>为什么还要改名</b>：内容根往往是作者自己的目录名（实测有 <c>Sigrika Ball Full</c> 这种），
    /// 直接拿来当 Mod 目录名既认不出是哪个包，也和「整个包当一个 Mod 装」对不上。内容一个字节都不动，
    /// 只换最外层的名字；改不了名（撞名 / 被占用）就保留原名 —— 名字难看也比丢内容好。
    /// </para>
    ///
    /// <para>
    /// 认不出「包名那一层」时（<c>&lt;guid&gt;</c> 下没有唯一的目录）不改名，只剥包装层 —— 那种形状
    /// 没有可信的包名可用，名字留给安装向导里的目录树。
    /// </para>
    /// </summary>
    public static DirectoryInfo ResolveInstallRoot(DirectoryInfo extractedRoot)
    {
        ArgumentNullException.ThrowIfNull(extractedRoot);

        // <guid> 这层是 JASM 自己造的纯包装：下面恰好一个目录时，那就是用户拖进来的包本身
        var packageRoot = GetSingleSubFolder(extractedRoot);

        var contentRoot = ResolveContentRoot(packageRoot ?? extractedRoot);

        return packageRoot is null
            ? contentRoot
            : RenameToPackageName(contentRoot, packageRoot);
    }

    /// <summary>
    /// 把内容根改名叫包名。<c>contentRoot</c> 已经是包名那一层（或更上层）时原样返回。
    /// <b>绝不抛异常</b>：改不了名只是名字不好看，不能因此让整单安装失败。
    /// </summary>
    private static DirectoryInfo RenameToPackageName(DirectoryInfo contentRoot, DirectoryInfo packageRoot)
    {
        if (string.Equals(contentRoot.FullName, packageRoot.FullName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentRoot.Name, packageRoot.Name, StringComparison.OrdinalIgnoreCase))
            return contentRoot;

        try
        {
            if (contentRoot.Parent is not { } parent)
                return contentRoot;

            var target = Path.Combine(parent.FullName, packageRoot.Name);

            // 撞名（同层已有同名的文件/目录）就不动：那多半是这一层本来就摆着别的东西
            if (Directory.Exists(target) || File.Exists(target))
                return contentRoot;

            contentRoot.MoveTo(target);
            return new DirectoryInfo(target);
        }
        catch (Exception)
        {
            return contentRoot;
        }
    }

    /// <summary>恰好一个子目录时返回它，零个或多个（读不动也算）返回 <c>null</c>。不抛异常。</summary>
    private static DirectoryInfo? GetSingleSubFolder(DirectoryInfo folder)
    {
        try
        {
            var subFolders = folder.GetDirectories();
            return subFolders.Length == 1 ? subFolders[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 顺着「只有一个子目录」的链条往下走，返回真正的包内容根；没有可剥的层时原样返回入参。
    ///
    /// <para>
    /// 判据只看<b>子目录的个数</b>：不比对目录名（比对名字会把「载荷里就一层目录、
    /// 名字与包名无关」的情况漏掉），也<b>不算同层的散文件</b> —— 实测达妮娅包的包装层里
    /// 躺着 <c>preview.png</c> 与一个站点 <c>.url</c> 快捷方式，连同那个包目录一共 3 个条目，
    /// 早先「这一层只准有一个条目」的判据到这儿就停下不走了，于是又退回老启发式。
    /// </para>
    ///
    /// <para>
    /// 往下剥之前先问一句 <see cref="LooksLikeSelfContainedModRoot"/>：这一层自己就是 Mod
    /// 就地停下 —— 否则「根带 ini + 一个变体子目录」的包会被剥成只剩子目录。
    /// </para>
    /// </summary>
    public static DirectoryInfo ResolveContentRoot(DirectoryInfo extractedRoot)
    {
        ArgumentNullException.ThrowIfNull(extractedRoot);

        var current = extractedRoot;

        for (var depth = 0; depth < MaxWrapperDepth; depth++)
        {
            if (LooksLikeSelfContainedModRoot(current))
                return current;

            DirectoryInfo[] subFolders;
            try
            {
                subFolders = current.GetDirectories();
            }
            catch (Exception)
            {
                // 读不动（权限 / 被占用）就别猜了：停在当前这层比抛出去好，后面还有原有的启发式兜底
                return current;
            }

            // 正好一个子目录 ⇒ 这是包装层，继续往下剥；
            // 零个（只剩散文件）或多个（一堆互不相干的 Mod）⇒ 剥不动了，交给调用方原有的启发式
            if (subFolders.Length != 1)
                return current;

            current = subFolders[0];
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