#include "Json.h"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

namespace jasm::uhd {
namespace {

constexpr int kMaxDepth = 64;

class Parser
{
public:
    Parser(const std::string& text, std::string* error)
        : _text(text), _error(error)
    {
    }

    bool Parse(JsonValue& out)
    {
        SkipWhitespace();
        if (!ParseValue(out, 0))
            return false;

        SkipWhitespace();
        if (_pos != _text.size())
            return Fail("trailing characters");

        return true;
    }

private:
    bool Fail(const char* message)
    {
        if (_error != nullptr)
        {
            char buffer[128] = {};
            std::snprintf(buffer, sizeof(buffer), "%s at offset %zu", message, _pos);
            *_error = buffer;
        }
        return false;
    }

    void SkipWhitespace()
    {
        while (_pos < _text.size())
        {
            const char c = _text[_pos];
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                break;
            ++_pos;
        }
    }

    bool Consume(char expected)
    {
        if (_pos >= _text.size() || _text[_pos] != expected)
            return false;
        ++_pos;
        return true;
    }

    bool ParseValue(JsonValue& out, int depth)
    {
        if (depth > kMaxDepth)
            return Fail("nesting too deep");

        SkipWhitespace();
        if (_pos >= _text.size())
            return Fail("unexpected end");

        const char c = _text[_pos];
        if (c == '{')
            return ParseObject(out, depth);
        if (c == '[')
            return ParseArray(out, depth);
        if (c == '"')
        {
            out.type = JsonValue::Type::String;
            return ParseString(out.stringValue);
        }
        if (c == 't')
            return ParseLiteral("true", true, out);
        if (c == 'f')
            return ParseLiteral("false", false, out);
        if (c == 'n')
            return ParseNull(out);
        if (c == '-' || (c >= '0' && c <= '9'))
            return ParseNumber(out);

        return Fail("unexpected character");
    }

    bool ParseLiteral(const char* literal, bool value, JsonValue& out)
    {
        const std::size_t length = std::char_traits<char>::length(literal);
        if (_text.compare(_pos, length, literal) != 0)
            return Fail("invalid literal");

        _pos += length;
        out.type = JsonValue::Type::Bool;
        out.boolValue = value;
        return true;
    }

    bool ParseNull(JsonValue& out)
    {
        if (_text.compare(_pos, 4, "null") != 0)
            return Fail("invalid literal");

        _pos += 4;
        out.type = JsonValue::Type::Null;
        return true;
    }

    bool ParseNumber(JsonValue& out)
    {
        const std::size_t start = _pos;
        if (_text[_pos] == '-')
            ++_pos;

        while (_pos < _text.size())
        {
            const char c = _text[_pos];
            if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                ++_pos;
            else
                break;
        }

        const std::string token = _text.substr(start, _pos - start);
        char* end = nullptr;
        const double value = std::strtod(token.c_str(), &end);
        if (end == nullptr || *end != '\0')
            return Fail("invalid number");

        out.type = JsonValue::Type::Number;
        out.numberValue = value;
        return true;
    }

    bool ParseString(std::string& out)
    {
        if (!Consume('"'))
            return Fail("expected string");

        out.clear();
        while (_pos < _text.size())
        {
            const char current = _text[_pos++];
            if (current == '"')
                return true;

            if (current != '\\')
            {
                out.push_back(current);
                continue;
            }

            if (_pos >= _text.size())
                return Fail("unterminated escape");

            const char escaped = _text[_pos++];
            switch (escaped)
            {
            case '"': out.push_back('"'); break;
            case '\\': out.push_back('\\'); break;
            case '/': out.push_back('/'); break;
            case 'b': out.push_back('\b'); break;
            case 'f': out.push_back('\f'); break;
            case 'n': out.push_back('\n'); break;
            case 'r': out.push_back('\r'); break;
            case 't': out.push_back('\t'); break;
            case 'u':
                if (!ParseUnicodeEscape(out))
                    return false;
                break;
            default:
                return Fail("invalid escape");
            }
        }

        return Fail("unterminated string");
    }

    bool ReadHex4(uint32_t& value)
    {
        if (_pos + 4 > _text.size())
            return false;

        value = 0;
        for (int i = 0; i < 4; ++i)
        {
            const char c = _text[_pos++];
            uint32_t digit = 0;
            if (c >= '0' && c <= '9')
                digit = static_cast<uint32_t>(c - '0');
            else if (c >= 'a' && c <= 'f')
                digit = static_cast<uint32_t>(c - 'a') + 10;
            else if (c >= 'A' && c <= 'F')
                digit = static_cast<uint32_t>(c - 'A') + 10;
            else
                return false;

            value = (value << 4) | digit;
        }
        return true;
    }

    bool ParseUnicodeEscape(std::string& out)
    {
        uint32_t codePoint = 0;
        if (!ReadHex4(codePoint))
            return Fail("invalid \\u escape");

        // 代理对：高位 0xD800-0xDBFF + 低位 0xDC00-0xDFFF
        if (codePoint >= 0xD800 && codePoint <= 0xDBFF && _pos + 6 <= _text.size()
            && _text[_pos] == '\\' && _text[_pos + 1] == 'u')
        {
            const std::size_t saved = _pos;
            _pos += 2;
            uint32_t low = 0;
            if (ReadHex4(low) && low >= 0xDC00 && low <= 0xDFFF)
            {
                codePoint = 0x10000 + ((codePoint - 0xD800) << 10) + (low - 0xDC00);
            }
            else
            {
                _pos = saved;
            }
        }

        AppendUtf8(out, codePoint);
        return true;
    }

    static void AppendUtf8(std::string& out, uint32_t codePoint)
    {
        if (codePoint <= 0x7F)
        {
            out.push_back(static_cast<char>(codePoint));
        }
        else if (codePoint <= 0x7FF)
        {
            out.push_back(static_cast<char>(0xC0 | (codePoint >> 6)));
            out.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
        }
        else if (codePoint <= 0xFFFF)
        {
            out.push_back(static_cast<char>(0xE0 | (codePoint >> 12)));
            out.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
        }
        else
        {
            out.push_back(static_cast<char>(0xF0 | (codePoint >> 18)));
            out.push_back(static_cast<char>(0x80 | ((codePoint >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
        }
    }

    bool ParseObject(JsonValue& out, int depth)
    {
        if (!Consume('{'))
            return Fail("expected object");

        out.type = JsonValue::Type::Object;
        out.objectItems.clear();
        SkipWhitespace();
        if (Consume('}'))
            return true;

        while (true)
        {
            SkipWhitespace();
            std::string key;
            if (!ParseString(key))
                return false;

            SkipWhitespace();
            if (!Consume(':'))
                return Fail("expected ':'");

            JsonValue value;
            if (!ParseValue(value, depth + 1))
                return false;
            out.objectItems.emplace_back(std::move(key), std::move(value));

            SkipWhitespace();
            if (Consume(','))
                continue;
            if (Consume('}'))
                return true;

            return Fail("expected ',' or '}'");
        }
    }

    bool ParseArray(JsonValue& out, int depth)
    {
        if (!Consume('['))
            return Fail("expected array");

        out.type = JsonValue::Type::Array;
        out.arrayItems.clear();
        SkipWhitespace();
        if (Consume(']'))
            return true;

        while (true)
        {
            JsonValue value;
            if (!ParseValue(value, depth + 1))
                return false;
            out.arrayItems.push_back(std::move(value));

            SkipWhitespace();
            if (Consume(','))
                continue;
            if (Consume(']'))
                return true;

            return Fail("expected ',' or ']'");
        }
    }

    const std::string& _text;
    std::string* _error;
    std::size_t _pos = 0;
};

}  // namespace

const JsonValue* JsonValue::Find(const std::string& key) const
{
    if (type != Type::Object)
        return nullptr;

    for (const auto& item : objectItems)
    {
        if (item.first == key)
            return &item.second;
    }
    return nullptr;
}

const std::string* JsonValue::GetString() const
{
    return type == Type::String ? &stringValue : nullptr;
}

bool JsonValue::GetInt(int* value) const
{
    if (value == nullptr || type != Type::Number)
        return false;
    if (numberValue < -2147483648.0 || numberValue > 2147483647.0)
        return false;
    if (std::floor(numberValue) != numberValue)
        return false;

    *value = static_cast<int>(numberValue);
    return true;
}

bool ParseJson(const std::string& text, JsonValue& out, std::string* error)
{
    if (error != nullptr)
        error->clear();

    Parser parser(text, error);
    return parser.Parse(out);
}

}  // namespace jasm::uhd
