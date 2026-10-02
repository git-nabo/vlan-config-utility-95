using System;
using System.Collections.Generic;
using System.Text;

namespace VlanConfig95.Core
{
    /// <summary>
    /// A single parsed record from our PowerShell transport format.
    /// </summary>
    public sealed class Record
    {
        private readonly List<string> _fields;

        public string Kind;

        public Record(string kind, List<string> fields)
        {
            Kind = kind;
            _fields = fields;
        }

        /// <summary>
        /// Returns the field at the given index, where index 0 is the FIRST field
        /// after the record kind. (The kind token itself is not addressable.)
        /// </summary>
        public string Get(int index)
        {
            if (index < 0 || index >= _fields.Count)
                return string.Empty;
            return _fields[index];
        }

        public int Count
        {
            get { return _fields.Count; }
        }
    }

    /// <summary>
    /// Result of parsing a PowerShell payload: a set of typed records plus an optional
    /// script-level error.
    /// </summary>
    public sealed class RecordSet
    {
        internal readonly List<Record> _records = new List<Record>();

        public string Error;

        public IList<Record> Records
        {
            get { return _records; }
        }

        public IEnumerable<Record> OfKind(string kind)
        {
            foreach (Record r in _records)
            {
                if (string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    yield return r;
            }
        }
    }

    /// <summary>
    /// Parser for the escaped pipe-delimited payload emitted by our PowerShell scripts.
    /// Escaping: '^' -&gt; "^^", '|' -&gt; "^P", CR -&gt; "^R", LF -&gt; "^N".
    /// Chosen over JSON so no extra deserialisation assembly is required.
    /// </summary>
    public static class RecordSetParser
    {
        public static RecordSet Parse(string payload)
        {
            RecordSet set = new RecordSet();
            if (string.IsNullOrEmpty(payload))
                return set;

            string[] lines = payload.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (string line in lines)
            {
                if (line.Length == 0)
                    continue;

                List<string> fields = SplitFields(line);
                if (fields.Count == 0)
                    continue;

                string kind = fields[0];
                fields.RemoveAt(0);

                if (kind == "!ERR")
                {
                    set.Error = fields.Count > 0 ? fields[0] : "Unknown error";
                    continue;
                }

                set._records.Add(new Record(kind, fields));
            }

            return set;
        }

        private static List<string> SplitFields(string line)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '^' && i + 1 < line.Length)
                {
                    char next = line[i + 1];
                    switch (next)
                    {
                        case '^':
                            current.Append('^');
                            i++;
                            continue;
                        case 'P':
                            current.Append('|');
                            i++;
                            continue;
                        case 'R':
                            current.Append('\r');
                            i++;
                            continue;
                        case 'N':
                            current.Append('\n');
                            i++;
                            continue;
                    }
                }

                if (c == '|')
                {
                    result.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }

                current.Append(c);
            }

            result.Add(current.ToString());
            return result;
        }
    }
}