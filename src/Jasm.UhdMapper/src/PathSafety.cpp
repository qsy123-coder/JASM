#include "PathSafety.h"

#include <cwchar>
#include <string.h>
#include <vector>

#include <Windows.h>

namespace jasm::uhd {

std::wstring GetFullPath(const std::wstring& path)
{
    if (path.empty())
        return {};

    DWORD required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (required == 0)
        return {};

    std::vector<wchar_t> buffer(required + 1, L'\0');
    const DWORD written = GetFullPathNameW(path.c_str(), required + 1, buffer.data(), nullptr);
    if (written == 0 || written > required)
        return {};

    return std::wstring(buffer.data(), written);
}

bool IsUnderDirectory(const std::wstring& file, const std::wstring& directory)
{
    std::wstring fullFile = GetFullPath(file);
    std::wstring fullDirectory = GetFullPath(directory);
    if (fullFile.empty() || fullDirectory.empty())
        return false;

    while (!fullDirectory.empty() && (fullDirectory.back() == L'\\' || fullDirectory.back() == L'/'))
        fullDirectory.pop_back();

    if (fullDirectory.empty() || fullFile.size() <= fullDirectory.size())
        return false;

    if (_wcsnicmp(fullFile.c_str(), fullDirectory.c_str(), fullDirectory.size()) != 0)
        return false;

    return fullFile[fullDirectory.size()] == L'\\' || fullFile[fullDirectory.size()] == L'/';
}

bool HasDdsExtension(const std::wstring& path)
{
    const auto dot = path.find_last_of(L'.');
    if (dot == std::wstring::npos)
        return false;

    return _wcsicmp(path.c_str() + dot, L".dds") == 0;
}

}  // namespace jasm::uhd
