using System.Text.Json;

namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>CLI 的 NDJSON 事件种类。</summary>
public enum WuwaModCliEventKind
{
    Unknown,
    Start,
    Progress,
    Log,
    Finish
}

/// <summary>
/// 一行 NDJSON 事件。上游承诺的形态是
/// <c>{"event":"start","mode":"fix","total":3}</c> 这类扁平对象；
/// 这里只取浮窗需要的字段，其余字段一律忽略。
/// </summary>
public sealed record WuwaModCliEvent(
    WuwaModCliEventKind Kind,
    string? Mode = null,
    int? Total = null,
    int? Current = null,
    double? Percent = null,
    string? Level = null,
    string? Message = null,
    bool? Success = null,
    string RawLine = "")
{
    /// <summary>
    /// 解析一行。空行/坏 JSON/缺 event 都返回 false；未知 event 返回 true + <see cref="WuwaModCliEventKind.Unknown"/>，
    /// 由调用方当成一条普通日志处理 —— 上游以后加事件类型时不该让整个修复停摆。
    /// </summary>
    public static bool TryParse(string? line, out WuwaModCliEvent? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.Trim().TrimStart('﻿');
        if (trimmed.Length == 0)
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("event", out var eventProperty)
                || eventProperty.ValueKind != JsonValueKind.String)
                return false;

            var eventName = eventProperty.GetString();
            if (string.IsNullOrWhiteSpace(eventName))
                return false;

            switch (eventName)
            {
                case "start":
                    result = new WuwaModCliEvent(
                        WuwaModCliEventKind.Start,
                        Mode: GetString(root, "mode"),
                        Total: GetInt(root, "total"),
                        RawLine: trimmed);
                    return true;

                case "progress":
                    result = new WuwaModCliEvent(
                        WuwaModCliEventKind.Progress,
                        Total: GetInt(root, "total"),
                        Current: GetInt(root, "current"),
                        Percent: GetDouble(root, "percent"),
                        RawLine: trimmed);
                    return true;

                case "log":
                    result = new WuwaModCliEvent(
                        WuwaModCliEventKind.Log,
                        Level: GetString(root, "level"),
                        Message: GetString(root, "message"),
                        RawLine: trimmed);
                    return true;

                case "finish":
                    result = new WuwaModCliEvent(
                        WuwaModCliEventKind.Finish,
                        Message: GetString(root, "message"),
                        Success: GetBool(root, "success"),
                        RawLine: trimmed);
                    return true;

                default:
                    // 上游还会发 stage / file_start / file_skipped 这类扩展事件。
                    // 能抽出人话就用它（reason/description/message），抽不到才回退到原始 JSON；
                    // 直接把整行 JSON 塞进日志会让控制台很难看，也不方便用户截图反馈。
                    result = new WuwaModCliEvent(
                        WuwaModCliEventKind.Unknown,
                        Message: GetString(root, "reason")
                                 ?? GetString(root, "description")
                                 ?? GetString(root, "message")
                                 ?? trimmed,
                        RawLine: trimmed);
                    return true;
            }
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}