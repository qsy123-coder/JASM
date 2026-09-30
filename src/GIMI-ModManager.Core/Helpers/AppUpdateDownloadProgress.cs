// 显式 using：本文件所在的 Core/Helpers 下多个文件会被别处以「源链接」方式编译，那些工程
// 未必开了 ImplicitUsings（见 JASM.AutoUpdater.csproj），所以这里也自带一份。
using System;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 一次更新包下载的进度快照，由下载器按固定节流间隔抛出（见 <c>AppUpdateDownloader</c>）。
///
/// 是 <c>readonly record struct</c>：它每秒要穿过几次线程边界，值语义 + 不分配正合适，
/// 且 UI 那侧直接比较两个快照就能判断"进度有没有变"。
/// </summary>
/// <param name="BytesReceived">已落盘的字节数（含断点续传时已有的那部分）。</param>
/// <param name="TotalBytes">总字节数；<c>0</c> = 未知（服务器没给 Content-Length，清单也没写 sizeBytes）。</param>
/// <param name="BytesPerSecond">这一段间隔内的平均速度；<c>&lt;= 0</c> = 不显示速度。</param>
public readonly record struct AppUpdateDownloadProgress(long BytesReceived, long TotalBytes,
    double BytesPerSecond)
{
    /// <summary>
    /// 已完成百分比（0–100）。总量未知时返回 <c>0</c>：此时进度条停 0 而不是假装走完 ——
    /// 进度条 UI 是可空值的，调用方更应该把它切成不确定态。
    /// </summary>
    public double Percent => TotalBytes > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100)
        : 0;

    /// <summary>
    /// 给人看的一行文案，例如 <c>下载中 45.3 MB / 120.0 MB（1.2 MB/s）</c>。
    ///
    /// 用 MB/GB 而不是像 ModEnv 那样列原始字节数：更新包是百 MB 级，一串 8 位数字既读不出量级、
    /// 也看不出还有多久。百分比那部分由进度条本体承担，这里只说"已下载多少 / 一共多少"。
    /// </summary>
    public string ToDisplayText()
    {
        var received = FormatSize(BytesReceived);
        var total = TotalBytes > 0 ? FormatSize(TotalBytes) : "未知";
        var text = $"下载中 {received} / {total}";

        if (BytesPerSecond <= 0)
            return text;

        var speed = BytesPerSecond >= 1024 * 1024
            ? $"{BytesPerSecond / 1024 / 1024:N1} MB/s"
            : $"{BytesPerSecond / 1024:N0} KB/s";

        return $"{text}（{speed}）";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024 / 1024:N2} GB";

        return bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024:N1} MB"
            : $"{bytes / 1024d:N0} KB";
    }
}