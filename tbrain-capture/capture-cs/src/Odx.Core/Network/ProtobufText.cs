using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>
/// Protobuf text format (as Source 2's protobuf bindings print a message: an optional "Name [id]" line, then "{ … }") to
/// JSON. Without the .proto there is no field type, so: a field seen more than once becomes an array; numbers become
/// integers or doubles, true/false booleans, bare identifiers (enum values) strings; a quoted value that is valid UTF-8
/// without control characters other than tab/newline is a string, any other is {"$bytes": "&lt;base64&gt;"}.
/// </summary>
public static class ProtobufText
{
    public static JsonObject ToJson(string text)
    {
        int i = text.IndexOf('{');
        if (i < 0) return [];
        i++;
        return ReadFields(text, ref i);
    }

    private static JsonObject ReadFields(string s, ref int i)
    {
        var fields = new JsonObject();
        while (true)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] == '}')
            {
                i++;
                return fields;
            }
            int start = i;
            while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '.' or '[' or ']')) i++;
            string name = s[start..i];
            if (name.Length == 0) throw new FormatException($"protobuf text: field name expected at {i}");
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ':') i++;
            SkipSpace(s, ref i);
            JsonNode? value;
            if (i < s.Length && s[i] == '{')
            {
                i++;
                value = ReadFields(s, ref i);
            }
            else value = ReadScalar(s, ref i);
            Add(fields, name, value);
        }
    }

    private static void Add(JsonObject fields, string name, JsonNode? value)
    {
        if (!fields.TryGetPropertyValue(name, out var existing))
        {
            fields[name] = value;
            return;
        }
        // A single value is never an array, so an array here is this field's earlier repetitions.
        if (existing is JsonArray repeated)
        {
            repeated.Add(value);
            return;
        }
        fields[name] = new JsonArray(existing!.DeepClone(), value);
    }

    private static JsonNode? ReadScalar(string s, ref int i)
    {
        if (s[i] == '"' || s[i] == '\'') return Quoted(s, ref i);
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '}') i++;
        string token = s[start..i];
        if (token is "true" or "false") return token == "true";
        if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
        if (ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u)) return u;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        return token;
    }

    private static JsonNode Quoted(string s, ref int i)
    {
        char quote = s[i++];
        var bytes = new List<byte>();
        Span<byte> utf8 = stackalloc byte[4];
        while (i < s.Length && s[i] != quote)
        {
            char c = s[i++];
            if (c != '\\')
            {
                int n = Encoding.UTF8.GetBytes([c], utf8);
                for (int k = 0; k < n; k++) bytes.Add(utf8[k]);
                continue;
            }
            char e = s[i++];
            switch (e)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case 'x':
                {
                    int start = i;
                    while (i < s.Length && i - start < 2 && Uri.IsHexDigit(s[i])) i++;
                    bytes.Add(byte.Parse(s[start..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    break;
                }
                case >= '0' and <= '7':
                {
                    int value = e - '0';
                    for (int k = 0; k < 2 && i < s.Length && s[i] is >= '0' and <= '7'; k++) value = value * 8 + (s[i++] - '0');
                    bytes.Add((byte)value);
                    break;
                }
                default: bytes.Add((byte)e); break;
            }
        }
        i++;
        byte[] raw = [.. bytes];
        try
        {
            string text = new UTF8Encoding(false, true).GetString(raw);
            if (!text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return text;
        }
        catch (DecoderFallbackException)
        {
        }
        return new JsonObject { ["$bytes"] = Convert.ToBase64String(raw) };
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}
