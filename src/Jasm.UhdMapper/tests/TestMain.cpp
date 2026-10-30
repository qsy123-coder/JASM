// M1 原生单测：CRC32C、贴图 hash、JSON/manifest 解析、别名冲突、路径安全。
// 不引入测试框架：CI 上 ctest 直接看进程退出码。

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include <Windows.h>
#include <d3d11.h>

#include "AliasTable.h"
#include "Crc32c.h"
#include "Json.h"
#include "Manifest.h"
#include "PathSafety.h"
#include "TextureHash.h"

using namespace jasm::uhd;

static int g_failures = 0;

#define CHECK(condition)                                                                  \
    do                                                                                    \
    {                                                                                     \
        if (!(condition))                                                                 \
        {                                                                                 \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition);              \
            ++g_failures;                                                                 \
        }                                                                                 \
    } while (0)

static void TestCrc32c()
{
    CHECK(Crc32c("123456789", 9) == 0xE3069283u);
    CHECK(Crc32c("", 0) == 0x00000000u);

    // 分块更新必须与一次性计算一致
    const char* text = "123456789";
    uint32_t running = Crc32cUpdate(0xFFFFFFFFu, text, 4);
    running = Crc32cUpdate(running, text + 4, 5);
    CHECK(~running == 0xE3069283u);
}

static void TestHashes()
{
    uint32_t hash = 0;
    CHECK(ParseHashHex("1998df83", &hash) && hash == 0x1998df83u);
    CHECK(ParseHashHex("DD1F9BC8", &hash) && hash == 0xDD1F9BC8u);
    CHECK(!ParseHashHex("00000000", &hash));
    CHECK(!ParseHashHex("c62d648", &hash));
    CHECK(!ParseHashHex("zzzzzzzz", &hash));
    CHECK(!ParseHashHex("1998df830", &hash));
    CHECK(HashToHex(0x1998df83u) == "1998df83");
}

static void TestRowLayout()
{
    UINT rowBytes = 0;
    UINT rowCount = 0;

    CHECK(GetRowLayout(DXGI_FORMAT_BC7_UNORM, 4096, 4096, &rowBytes, &rowCount));
    CHECK(rowBytes == 16384);
    CHECK(rowCount == 1024);

    CHECK(GetRowLayout(DXGI_FORMAT_BC1_UNORM, 8, 8, &rowBytes, &rowCount));
    CHECK(rowBytes == 16);
    CHECK(rowCount == 2);

    CHECK(GetRowLayout(DXGI_FORMAT_R8G8B8A8_UNORM, 100, 10, &rowBytes, &rowCount));
    CHECK(rowBytes == 400);
    CHECK(rowCount == 10);

    CHECK(!GetRowLayout(DXGI_FORMAT_UNKNOWN, 8, 8, &rowBytes, &rowCount));
}

static void TestTextureHashIgnoresPadding()
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = 8;
    desc.Height = 8;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_BC7_UNORM;
    desc.SampleDesc.Count = 1;

    // 8x8 BC7 = 2x2 blocks，每行 32 字节；pitch 给 64，后 32 字节是 padding。
    std::vector<uint8_t> first(2 * 64, 0xAA);
    std::vector<uint8_t> second(2 * 64, 0x55);

    for (int i = 0; i < 32; ++i)
    {
        first[i] = static_cast<uint8_t>(i + 1);
        second[i] = static_cast<uint8_t>(i + 1);
        first[64 + i] = static_cast<uint8_t>(i + 100);
        second[64 + i] = static_cast<uint8_t>(i + 100);
    }

    D3D11_SUBRESOURCE_DATA dataFirst{};
    dataFirst.pSysMem = first.data();
    dataFirst.SysMemPitch = 64;

    D3D11_SUBRESOURCE_DATA dataSecond{};
    dataSecond.pSysMem = second.data();
    dataSecond.SysMemPitch = 64;

    uint32_t hashFirst = 0;
    uint32_t hashSecond = 0;
    CHECK(ComputeTextureDataHash(desc, &dataFirst, &hashFirst));
    CHECK(ComputeTextureDataHash(desc, &dataSecond, &hashSecond));
    CHECK(hashFirst == hashSecond);

    // 数据 hash 只覆盖有效行宽（padding 不参与）
    uint32_t running = Crc32cUpdate(0xFFFFFFFFu, first.data(), 32);
    running = Crc32cUpdate(running, first.data() + 64, 32);
    CHECK(hashFirst == ~running);

    // 最终 hash = CRC32C(desc 字节, seed=data hash)
    uint32_t full = 0;
    CHECK(ComputeTextureHash(desc, &dataFirst, 0, 0, &full));
    CHECK(full == CombineDescHash(hashFirst, desc, 0, 0));

    // 宽高等于屏幕分辨率时，desc hash 用 'SRES' 替换宽高
    uint32_t screenSized = 0;
    CHECK(ComputeTextureHash(desc, &dataFirst, 8, 8, &screenSized));
    CHECK(screenSized != full);

    // 没有初始数据时不计算
    CHECK(!ComputeTextureDataHash(desc, nullptr, &hashFirst));

    // array / MSAA 纹理仍按 mip0 首 slice 计算（与 3DMigoto 一致）
    desc.ArraySize = 2;
    CHECK(ComputeTextureDataHash(desc, &dataFirst, &hashFirst));
    desc.ArraySize = 1;
    desc.SampleDesc.Count = 4;
    CHECK(ComputeTextureDataHash(desc, &dataFirst, &hashFirst));
}

static void TestJson()
{
    const char* text = R"({"schemaVersion":1,"role":"\u7433\u5948","list":[1,2,3],"flag":true,"n":null})";
    JsonValue root;
    std::string error;
    CHECK(ParseJson(text, root, &error));

    int schema = 0;
    const JsonValue* schemaValue = root.Find("schemaVersion");
    CHECK(schemaValue != nullptr && schemaValue->GetInt(&schema) && schema == 1);

    const JsonValue* role = root.Find("role");
    CHECK(role != nullptr && role->GetString() != nullptr && *role->GetString() == "琳奈");

    const JsonValue* list = root.Find("list");
    CHECK(list != nullptr && list->IsArray() && list->arrayItems.size() == 3);

    CHECK(!ParseJson("{", root, &error));
    CHECK(!ParseJson("{\"a\":}", root, &error));
    CHECK(!ParseJson("{} trailing", root, &error));
}

static std::string BuildManifestJson(const std::string& extraTexture = "")
{
    return std::string(R"({
      "schemaVersion": 1,
      "game": "WuWa",
      "role": "琳奈",
      "roleInternalName": "lynae",
      "mods": [
        {
          "name": "Swimsuit",
          "rootPath": "C:\\mods\\lynae",
          "iniFiles": ["C:\\mods\\lynae\\mod.ini"],
          "textures": [
            {
              "hdHash": "1998df83",
              "resourceSection": "ResourceDiffuse",
              "ddsPath": "C:\\mods\\lynae\\tex\\diffuse.dds",
              "overrideSection": "TextureOverrideTexture8",
              "uhdHashes": ["dd1f9bc8", "1998df83", "deadbeef"]
            })"
           + extraTexture + R"(
          ]
        }
      ]
    })");
}

static void TestManifest()
{
    Manifest manifest;
    std::string error;
    CHECK(ParseManifestJson(BuildManifestJson(), manifest, &error) == ManifestStatus::Ok);
    CHECK(manifest.schemaVersion == 1);
    CHECK(manifest.role == L"琳奈");
    CHECK(manifest.mods.size() == 1);
    CHECK(manifest.mods[0].textures.size() == 1);

    const ManifestTexture& texture = manifest.mods[0].textures[0];
    CHECK(texture.hdHashValid && texture.hdHash == 0x1998df83u);
    CHECK(texture.ddsPath == L"C:\\mods\\lynae\\tex\\diffuse.dds");
    CHECK(texture.uhdHashes.size() == 3);
    CHECK(texture.uhdHashes[0] == 0xdd1f9bc8u);

    CHECK(ParseManifestJson("{\"schemaVersion\":99}", manifest, &error)
          == ManifestStatus::UnsupportedSchema);
    CHECK(ParseManifestJson("{not json", manifest, &error) == ManifestStatus::InvalidJson);
}

static void TestAliasTable()
{
    Manifest manifest;
    std::string error;
    CHECK(ParseManifestJson(BuildManifestJson(), manifest, &error) == ManifestStatus::Ok);

    AliasTable table;
    table.Build(manifest);

    CHECK(table.IsHdHash(0x1998df83u));

    AliasTarget target;
    CHECK(table.TryGet(0xdd1f9bc8u, &target));
    CHECK(target.ddsPath == L"C:\\mods\\lynae\\tex\\diffuse.dds");

    // 与 HD hash 相同：交给 WWMI，不做替换
    CHECK(!table.TryGet(0x1998df83u, &target));

    // 同一 UHD hash 指向不同 DDS → ambiguous
    Manifest conflicting;
    CHECK(ParseManifestJson(BuildManifestJson(R"(,
            {
              "hdHash": "11111111",
              "ddsPath": "C:\\mods\\lynae\\tex\\other.dds",
              "uhdHashes": ["deadbeef"]
            })"),
                            conflicting, &error)
          == ManifestStatus::Ok);

    AliasTable conflictTable;
    conflictTable.Build(conflicting);
    CHECK(!conflictTable.TryGet(0xdeadbeefu, &target));
    CHECK(conflictTable.AmbiguousCount() >= 1);

    // 路径不在 mod root 下 → 跳过
    Manifest outside;
    CHECK(ParseManifestJson(R"({
      "schemaVersion": 1,
      "mods": [
        {
          "name": "Bad",
          "rootPath": "C:\\mods\\lynae",
          "textures": [
            {
              "hdHash": "22222222",
              "ddsPath": "C:\\elsewhere\\evil.dds",
              "uhdHashes": ["abcdef01"]
            }
          ]
        }
      ]
    })",
                            outside, &error)
          == ManifestStatus::Ok);

    AliasTable outsideTable;
    outsideTable.Build(outside);
    CHECK(!outsideTable.TryGet(0xabcdef01u, &target));
}

static void TestPathSafety()
{
    CHECK(IsUnderDirectory(L"C:\\mods\\lynae\\tex\\a.dds", L"C:\\mods\\lynae"));
    CHECK(!IsUnderDirectory(L"C:\\mods\\lynae2\\a.dds", L"C:\\mods\\lynae"));
    CHECK(!IsUnderDirectory(L"C:\\mods\\lynae", L"C:\\mods\\lynae"));
    CHECK(HasDdsExtension(L"C:\\x\\A.DDS"));
    CHECK(!HasDdsExtension(L"C:\\x\\A.png"));
    CHECK(!HasDdsExtension(L"C:\\x\\dds"));
}

int main()
{
    TestCrc32c();
    TestHashes();
    TestRowLayout();
    TestTextureHashIgnoresPadding();
    TestJson();
    TestManifest();
    TestAliasTable();
    TestPathSafety();

    if (g_failures == 0)
    {
        std::printf("all tests passed\n");
        return 0;
    }

    std::printf("%d test(s) failed\n", g_failures);
    return 1;
}
