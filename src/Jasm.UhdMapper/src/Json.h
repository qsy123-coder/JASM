#pragma once

#include <string>
#include <utility>
#include <vector>

namespace jasm::uhd {

// 只服务于 active-mod.json 的最小 JSON DOM。不追求完整 RFC 覆盖，
// 但要能安全解析 System.Text.Json 生成的清单（含转义、\uXXXX、嵌套）。
class JsonValue
{
public:
    enum class Type { Null, Bool, Number, String, Array, Object };

    Type type = Type::Null;
    bool boolValue = false;
    double numberValue = 0.0;
    std::string stringValue;
    std::vector<JsonValue> arrayItems;
    std::vector<std::pair<std::string, JsonValue>> objectItems;

    bool IsObject() const { return type == Type::Object; }
    bool IsArray() const { return type == Type::Array; }
    bool IsString() const { return type == Type::String; }
    bool IsNumber() const { return type == Type::Number; }

    const JsonValue* Find(const std::string& key) const;
    const std::string* GetString() const;
    bool GetInt(int* value) const;
};

bool ParseJson(const std::string& text, JsonValue& out, std::string* error);

}  // namespace jasm::uhd
