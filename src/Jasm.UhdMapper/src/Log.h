#pragma once

#include <string>

namespace jasm::uhd {

enum class LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
};

// 异步日志：钩子里只入队，不做文件 IO。路径由调用方给出（%LOCALAPPDATA%\JASM\UhdRuntime）。
void LogInit(const std::wstring& path, bool debugEnabled);
void LogShutdown();
void LogFlush();

void LogWrite(LogLevel level, const char* format, ...);
void LogWide(LogLevel level, const std::wstring& message);

}  // namespace jasm::uhd
