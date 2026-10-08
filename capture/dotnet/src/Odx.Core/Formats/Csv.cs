using System.Text;

namespace Odx.Core;

/// <summary>Minimal RFC 4180 reader/writer: quoted fields may hold commas, quotes and newlines.</summary>
public static class Csv
{
    public static (List<string> Header, List<Dictionary<string, string>> Rows) ReadFile(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false));
        var records = ReadRecords(reader);
        if (records.Count == 0) return ([], []);
        var header = records[0];
        var rows = new List<Dictionary<string, string>>(records.Count - 1);
        foreach (var rec in records.Skip(1))
        {
            var row = new Dictionary<string, string>(header.Count);
            for (int i = 0; i < header.Count; i++) row[header[i]] = i < rec.Count ? rec[i] : "";
            rows.Add(row);
        }
        return (header, rows);
    }

    public static List<List<string>> ReadRecords(TextReader reader)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            any = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append((char)c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
                any = false;
            }
            else
            {
                field.Append((char)c);
            }
        }
        if (any)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    public static string Field(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    public static string Line(IEnumerable<string> fields) => string.Join(",", fields.Select(Field)) + "\n";
}
