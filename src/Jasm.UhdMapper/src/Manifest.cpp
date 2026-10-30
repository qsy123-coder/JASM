#include "Manifest.h"

#include <cstdio>
#include <vector>

#include <Windows.h>

#include "Json.h"
#include "TextureHash.h"

namespace jasm::uhd {
namespace {

constexpr DWORD kMaxManifestBytes = 32u * 1024u * 1024u;
constexpr int kSupportedSchemaVersion = 1;

bool ReadFileUtf8(const std::wstring& path, std::string& out, std::string* error)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        if (error != nullptr)
            *error = "open failed";
        return false;
    }

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0
        || size.QuadPart > static_cast<LONGLONG>(kMaxManifestBytes))
    {
        CloseHandle(file);
        if (error != nullptr)
            *error = "bad size";
        return false;
    }

    out.resize(static_cast<std::size_t>(size.QuadPart));
    DWORD read = 0;
    const BOOL ok = ReadFile(file, out.data(), static_cast<DWORD>(out.size()), &read, nullptr);
    CloseHandle(file);

    if (!ok || static_cast<std::size_t>(read) != out.size())
    {
        if (error != nullptr)
            *error = "read failed";
        return false;
    }

    return true;
}

const JsonValue* FindMember(const JsonValue& object, const char* key)
{
    return object.IsObject() ? object.Find(key) : nullptr;
}

void ParseTexture(const JsonValue& value, ManifestTexture& texture)
{
    const JsonValue* hdHash = FindMember(value, "hdHash");
    if (hdHash != nullptr && hdHash->GetString() != nullptr)
        texture.hdHashValid = ParseHashHex(*hdHash->GetString(), &texture.hdHash);

    const JsonValue* ddsPath = FindMember(value, "ddsPath");
    if (ddsPath != nullptr && ddsPath->GetString() != nullptr)
        texture.ddsPath = Utf8ToWide(*ddsPath->GetString());

    const JsonValue* resource = FindMember(value, "resourceSection");
    if (resource != nullptr && resource->GetString() != nullptr)
        texture.resourceSection = Utf8ToWide(*resource->GetString());

    const JsonValue* overrideSection = FindMember(value, "overrideSection");
    if (overrideSection != nullptr && overrideSection->GetString() != nullptr)
        texture.overrideSection = Utf8ToWide(*overrideSection->GetString());

    const JsonValue* uhdHashes = FindMember(value, "uhdHashes");
    if (uhdHashes != nullptr && uhdHashes->IsArray())
    {
        for (const auto& item : uhdHashes->arrayItems)
        {
            uint32_t hash = 0;
            if (item.GetString() != nullptr && ParseHashHex(*item.GetString(), &hash))
                texture.uhdHashes.push_back(hash);
        }
    }
}

void ParseMod(const JsonValue& value, ManifestMod& mod)
{
    const JsonValue* name = FindMember(value, "name");
    if (name != nullptr && name->GetString() != nullptr)
        mod.name = Utf8ToWide(*name->GetString());

    const JsonValue* rootPath = FindMember(value, "rootPath");
    if (rootPath != nullptr && rootPath->GetString() != nullptr)
        mod.rootPath = Utf8ToWide(*rootPath->GetString());

    const JsonValue* textures = FindMember(value, "textures");
    if (textures == nullptr || !textures->IsArray())
        return;

    mod.textures.reserve(textures->arrayItems.size());
    for (const auto& item : textures->arrayItems)
    {
        ManifestTexture texture;
        ParseTexture(item, texture);
        mod.textures.push_back(std::move(texture));
    }
}

}  // namespace

std::size_t Manifest::TextureCount() const
{
    std::size_t count = 0;
    for (const auto& mod : mods)
        count += mod.textures.size();
    return count;
}

std::size_t Manifest::DdsCount() const
{
    std::size_t count = 0;
    for (const auto& mod : mods)
    {
        for (const auto& texture : mod.textures)
        {
            if (!texture.ddsPath.empty())
                ++count;
        }
    }
    return count;
}

ManifestStatus LoadManifest(const std::wstring& path, Manifest& out, std::string* error)
{
    out = Manifest{};
    if (error != nullptr)
        error->clear();

    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES)
        return ManifestStatus::Missing;

    std::string json;
    if (!ReadFileUtf8(path, json, error))
        return ManifestStatus::Unreadable;

    return ParseManifestJson(json, out, error);
}

ManifestStatus ParseManifestJson(const std::string& json, Manifest& out, std::string* error)
{
    out = Manifest{};
    if (error != nullptr)
        error->clear();

    JsonValue root;
    std::string parseError;
    if (!ParseJson(json, root, &parseError) || !root.IsObject())
    {
        if (error != nullptr)
            *error = parseError.empty() ? "root is not an object" : parseError;
        return ManifestStatus::InvalidJson;
    }

    const JsonValue* schema = FindMember(root, "schemaVersion");
    int schemaVersion = 0;
    if (schema == nullptr || !schema->GetInt(&schemaVersion)
        || schemaVersion != kSupportedSchemaVersion)
    {
        if (error != nullptr)
        {
            char buffer[96] = {};
            std::snprintf(buffer, sizeof(buffer), "unsupported schemaVersion=%d", schemaVersion);
            *error = buffer;
        }
        return ManifestStatus::UnsupportedSchema;
    }

    out.schemaVersion = schemaVersion;

    if (const JsonValue* game = FindMember(root, "game"); game != nullptr && game->GetString() != nullptr)
        out.game = Utf8ToWide(*game->GetString());
    if (const JsonValue* role = FindMember(root, "role"); role != nullptr && role->GetString() != nullptr)
        out.role = Utf8ToWide(*role->GetString());
    if (const JsonValue* internalName = FindMember(root, "roleInternalName");
        internalName != nullptr && internalName->GetString() != nullptr)
        out.roleInternalName = Utf8ToWide(*internalName->GetString());

    const JsonValue* mods = FindMember(root, "mods");
    if (mods != nullptr && mods->IsArray())
    {
        out.mods.reserve(mods->arrayItems.size());
        for (const auto& item : mods->arrayItems)
        {
            ManifestMod mod;
            ParseMod(item, mod);
            out.mods.push_back(std::move(mod));
        }
    }

    return ManifestStatus::Ok;
}

std::wstring Utf8ToWide(const std::string& text)
{
    if (text.empty())
        return {};

    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                           static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0)
        return {};

    std::wstring result(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
                        result.data(), length);
    return result;
}

std::string WideToUtf8(const std::wstring& text)
{
    if (text.empty())
        return {};

    const int length = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                                           nullptr, 0, nullptr, nullptr);
    if (length <= 0)
        return {};

    std::string result(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(),
                        length, nullptr, nullptr);
    return result;
}

}  // namespace jasm::uhd
