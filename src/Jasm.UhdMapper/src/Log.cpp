#include "Log.h"

#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <thread>

#include <Windows.h>

namespace jasm::uhd {
namespace {

constexpr std::size_t kMaxQueuedLines = 8192;
constexpr std::uintmax_t kRotateBytes = 8ull * 1024 * 1024;

std::mutex g_mutex;
std::condition_variable g_cv;
std::deque<std::string> g_queue;
std::thread g_writer;
std::ofstream g_file;
bool g_running = false;
bool g_debug = false;
std::size_t g_dropped = 0;

char LevelChar(LogLevel level)
{
    switch (level)
    {
    case LogLevel::Debug: return 'D';
    case LogLevel::Info: return 'I';
    case LogLevel::Warn: return 'W';
    case LogLevel::Error: return 'E';
    }
    return '?';
}

std::string MakePrefix(LogLevel level)
{
    SYSTEMTIME time{};
    GetLocalTime(&time);

    char buffer[64] = {};
    std::snprintf(buffer, sizeof(buffer), "[%04u-%02u-%02u %02u:%02u:%02u.%03u] [%c] ",
                  time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
                  time.wMilliseconds, LevelChar(level));
    return buffer;
}

void RotateIfNeeded(const std::wstring& path)
{
    std::error_code error;
    const std::uintmax_t size = std::filesystem::file_size(path, error);
    if (error || size < kRotateBytes)
        return;

    std::wstring backup = path + L".1";
    std::filesystem::remove(backup, error);
    std::filesystem::rename(path, backup, error);
}

void WriterLoop()
{
    std::unique_lock<std::mutex> lock(g_mutex);

    while (true)
    {
        g_cv.wait(lock, [] { return !g_running || !g_queue.empty(); });

        if (g_queue.empty() && !g_running)
            break;

        while (!g_queue.empty())
        {
            const std::string line = std::move(g_queue.front());
            g_queue.pop_front();
            lock.unlock();
            if (g_file.is_open())
            {
                g_file.write(line.data(), static_cast<std::streamsize>(line.size()));
                g_file.flush();
            }
            lock.lock();
        }
    }

    if (g_dropped > 0)
    {
        const std::string note = MakePrefix(LogLevel::Warn)
                                 + "log dropped=" + std::to_string(g_dropped) + " lines\n";
        lock.unlock();
        if (g_file.is_open())
        {
            g_file.write(note.data(), static_cast<std::streamsize>(note.size()));
            g_file.flush();
        }
        lock.lock();
    }

    if (g_file.is_open())
        g_file.close();
}

void Enqueue(std::string line)
{
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (!g_running)
            return;
        if (g_queue.size() >= kMaxQueuedLines)
        {
            ++g_dropped;
            return;
        }
        g_queue.push_back(std::move(line));
    }
    g_cv.notify_one();
}

}  // namespace

void LogInit(const std::wstring& path, bool debugEnabled)
{
    if (path.empty())
        return;

    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_running)
        return;

    std::error_code error;
    std::filesystem::create_directories(std::filesystem::path(path).parent_path(), error);
    RotateIfNeeded(path);

    g_file.open(std::filesystem::path(path), std::ios::out | std::ios::app | std::ios::binary);
    g_debug = debugEnabled;
    g_dropped = 0;
    g_running = true;
    g_writer = std::thread(WriterLoop);
}

void LogShutdown()
{
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (!g_running)
            return;
        g_running = false;
    }
    g_cv.notify_all();

    if (g_writer.joinable())
        g_writer.join();
}

void LogFlush()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file.is_open())
        g_file.flush();
}

void LogWrite(LogLevel level, const char* format, ...)
{
    if (level == LogLevel::Debug)
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (!g_debug)
            return;
    }

    char message[2048] = {};
    va_list args;
    va_start(args, format);
    std::vsnprintf(message, sizeof(message), format, args);
    va_end(args);

    Enqueue(MakePrefix(level) + message + "\n");
}

void LogWide(LogLevel level, const std::wstring& message)
{
    if (message.empty())
        return;

    const int length = WideCharToMultiByte(CP_UTF8, 0, message.data(),
                                           static_cast<int>(message.size()), nullptr, 0, nullptr,
                                           nullptr);
    if (length <= 0)
        return;

    std::string utf8(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, message.data(), static_cast<int>(message.size()), utf8.data(),
                        length, nullptr, nullptr);

    LogWrite(level, "%s", utf8.c_str());
}

}  // namespace jasm::uhd
