namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 把「短时间内连着来的多次刷新请求」合并成「正在跑的那一次 ＋ 最多一次补发」。
///
/// **为什么需要**：浮窗上勾选 Mod 会触发一次游戏内刷新（切前台 → 送 F10）。试装时用户常连着勾
/// 好几个 —— 连着发 N 次刷新就是让游戏重载 N 遍，又慢又容易被当成卡死。
/// 但「在跑的时候来的请求直接丢掉」也不行：最后一次勾选的结果就永远不会生效了。
/// 所以规则是：在跑 → 只记一笔「还有事没做」；跑完 → 若记过账就再发**一次**（也只发一次）。
///
/// 纯状态机：不碰线程、不碰时钟，「要不要真的发」完全由调用方按返回值决定。
/// </summary>
public sealed class RefreshCoalescer
{
    private readonly object _gate = new();
    private bool _inFlight;
    private bool _pending;

    /// <summary>是否有一次刷新正在跑（或已排上补发）。仅供测试与诊断。</summary>
    public bool IsInFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>
    /// 请求一次刷新。返回 <c>true</c> = **现在就去发**；<c>false</c> = 已合并进正在跑 / 待发的那一次，
    /// 调用方什么都不用做（结果照样会是最新的）。
    /// </summary>
    public bool Request()
    {
        lock (_gate)
        {
            if (_inFlight)
            {
                _pending = true;
                return false;
            }

            _inFlight = true;
            return true;
        }
    }

    /// <summary>
    /// 一次刷新结束（**成功或失败都要调**）。返回 <c>true</c> = 期间攒下的请求需要立刻补发一次，
    /// 调用方应马上再走一遍刷新、并再次调用本方法。
    ///
    /// 调用方必须把这句放在 <c>finally</c> 里：漏调一次，<see cref="Request"/> 就永远返回 <c>false</c>，
    /// 浮窗从此再也刷不动 —— 而且它不报错，用户看到的只是「点了没反应」。
    /// </summary>
    public bool Complete()
    {
        lock (_gate)
        {
            if (_pending)
            {
                // 刻意不清 _inFlight：补发的那一次紧接着就会来，状态要保持「在跑」，
                // 否则补发期间来的新请求会被误判成「可以立刻发」而并发出去。
                _pending = false;
                return true;
            }

            _inFlight = false;
            return false;
        }
    }
}