using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// Minimal JSON string escaping for the hand-written writers (manifest,
    /// Google Drive metadata). Parsing is done with JsonUtility.
    /// </summary>
    public static class UniLfsJsonUtil
    {
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Quote(string value)
        {
            return "\"" + Escape(value) + "\"";
        }
    }
}
