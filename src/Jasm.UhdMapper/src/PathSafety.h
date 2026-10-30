#pragma once

#include <string>

namespace jasm::uhd {

// 规范化绝对路径（失败返回空串）。
std::wstring GetFullPath(const std::wstring& path);

// file 是否位于 directory 之下（大小写不敏感，要求目录边界匹配）。
bool IsUnderDirectory(const std::wstring& file, const std::wstring& directory);

// 扩展名是否为 .dds（大小写不敏感）。
bool HasDdsExtension(const std::wstring& path);

}  // namespace jasm::uhd
