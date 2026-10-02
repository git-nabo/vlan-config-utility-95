using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace VlanConfig95.Core
{
    /// <summary>
    /// Persists network profiles as JSON in the current user's local application data
    /// folder, e.g. %LOCALAPPDATA%\VlanConfig95\profiles.json.
    ///
    /// Writing here never needs administrator rights. Only applying a profile to an
    /// adapter does.
    /// </summary>
    public static class ProfileStore
    {
        private static readonly List<NetworkProfile> _profiles = new List<NetworkProfile>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static IList<NetworkProfile> Profiles { get { return _profiles; } }

        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VlanConfig95");
            }
        }

        public static string FilePath { get { return Path.Combine(DirectoryPath, "profiles.json"); } }

        /// <summary>Shape of the profiles.json document.</summary>
        private sealed class ProfileFile
        {
            public int version { get; set; }
            public List<NetworkProfile> profiles { get; set; }
        }

        /// <summary>
        /// One profile as stored on disk. The two mode fields are read as text rather
        /// than as enums: JavaScriptSerializer maps a JSON string onto an enum by
        /// position, not by name, so "Dhcp" would silently become Static.
        /// </summary>
        private sealed class StoredProfile
        {
            public string name { get; set; }
            public string ipAddress { get; set; }
            public string subnetMask { get; set; }
            public string prefixLength { get; set; }
            public string gateway { get; set; }
            public string primaryDns { get; set; }
            public string secondaryDns { get; set; }
            public string vlanId { get; set; }
            public string description { get; set; }
            public string ipMode { get; set; }
            public string dnsMode { get; set; }

            public NetworkProfile ToProfile()
            {
                NetworkProfile p = new NetworkProfile();
                p.Name = Clean(name);
                p.IpAddress = Clean(ipAddress);
                p.SubnetMask = Clean(subnetMask);
                p.PrefixLength = Clean(prefixLength);
                p.Gateway = Clean(gateway);
                p.PrimaryDns = Clean(primaryDns);
                p.SecondaryDns = Clean(secondaryDns);
                p.VlanId = Clean(vlanId);
                p.Description = Clean(description);

                IpMode ip;
                DnsMode dns;
                p.IpConfig = Enum.TryParse(ipMode, true, out ip) ? ip : IpMode.Static;
                p.DnsConfig = Enum.TryParse(dnsMode, true, out dns) ? dns : DnsMode.Static;
                return p;
            }

            private static string Clean(string s)
            {
                return s == null ? string.Empty : s.Trim();
            }
        }

        private sealed class StoredFile
        {
            public int version { get; set; }
            public List<StoredProfile> profiles { get; set; }
        }

        /// <summary>
        /// Accepts either the wrapped document ({"version":1,"profiles":[...]}) or a
        /// bare array, so files written by either version load correctly.
        /// </summary>
        private static List<NetworkProfile> Deserialize(string json)
        {
            string trimmed = json.TrimStart();
            if (trimmed.Length == 0)
                return new List<NetworkProfile>();

            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                List<StoredProfile> bare = Json.Deserialize<List<StoredProfile>>(json);
                return Convert(bare);
            }

            StoredFile file = Json.Deserialize<StoredFile>(json);
            return file != null ? Convert(file.profiles) : new List<NetworkProfile>();
        }

        private static List<NetworkProfile> Convert(List<StoredProfile> stored)
        {
            List<NetworkProfile> result = new List<NetworkProfile>();
            if (stored == null)
                return result;

            foreach (StoredProfile s in stored)
            {
                if (s == null) continue;
                NetworkProfile p = s.ToProfile();
                if (p.Name.Length > 0)
                    result.Add(p);
            }

            return result;
        }

        /// <summary>Loads profiles from disk. A missing or unreadable file is not fatal.</summary>
        public static void Load(out string error)
        {
            error = null;
            _profiles.Clear();

            try
            {
                if (!File.Exists(FilePath)) return;

                foreach (NetworkProfile p in Deserialize(File.ReadAllText(FilePath, Encoding.UTF8)))
                    _profiles.Add(p);
            }
            catch (Exception ex)
            {
                error = "The profiles file could not be read: " + ex.Message;
            }
        }

        /// <summary>Writes the current profiles to disk.</summary>
        public static bool Save(out string error)
        {
            error = null;
            try
            {
                if (!Directory.Exists(DirectoryPath)) Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(FilePath, Serialize(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = "The profiles file could not be saved: " + ex.Message;
                return false;
            }
        }

        /// <summary>Produces the JSON document written to profiles.json.</summary>
        public static string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n  \"version\": 1,\r\n  \"profiles\": [\r\n");

            for (int i = 0; i < _profiles.Count; i++)
            {
                NetworkProfile p = _profiles[i];
                sb.Append("    {");
                sb.Append("\"name\":").Append(Quote(p.Name));
                sb.Append(",\"ipAddress\":").Append(Quote(p.IpAddress));
                sb.Append(",\"subnetMask\":").Append(Quote(p.SubnetMask));
                sb.Append(",\"prefixLength\":").Append(Quote(p.PrefixLength));
                sb.Append(",\"gateway\":").Append(Quote(p.Gateway));
                sb.Append(",\"primaryDns\":").Append(Quote(p.PrimaryDns));
                sb.Append(",\"secondaryDns\":").Append(Quote(p.SecondaryDns));
                sb.Append(",\"vlanId\":").Append(Quote(p.VlanId));
                sb.Append(",\"description\":").Append(Quote(p.Description));
                sb.Append(",\"ipMode\":").Append(Quote(p.IpConfig.ToString()));
                sb.Append(",\"dnsMode\":").Append(Quote(p.DnsConfig.ToString()));
                sb.Append("}");
                if (i < _profiles.Count - 1) sb.Append(",");
                sb.Append("\r\n");
            }

            sb.Append("  ]\r\n}");
            return sb.ToString();
        }

        private static string Quote(string value)
        {
            return Json.Serialize(value ?? string.Empty);
        }

        /// <summary>
        /// Parses a profiles document. Only expected fields are read; unknown keys are
        /// ignored and nothing from the file is ever executed. Every profile is
        /// validated and only valid ones are accepted.
        /// </summary>
        public static int Import(string json, out string error, out List<string> rejected)
        {
            error = null;
            rejected = new List<string>();

            if (string.IsNullOrEmpty(json)) { error = "The selected file is empty."; return 0; }

            List<NetworkProfile> parsed;
            try
            {
                parsed = Deserialize(json);
            }
            catch (Exception ex)
            {
                error = "The file is not a valid profiles document: " + ex.Message;
                return 0;
            }

            if (parsed == null || parsed.Count == 0)
            {
                error = "The file does not contain any profiles.";
                return 0;
            }

            int added = 0;
            foreach (NetworkProfile p in parsed)
            {
                if (p == null) continue;

                p.Name = (p.Name ?? string.Empty).Trim();
                p.IpAddress = (p.IpAddress ?? string.Empty).Trim();
                p.SubnetMask = (p.SubnetMask ?? string.Empty).Trim();
                p.PrefixLength = (p.PrefixLength ?? string.Empty).Trim();
                p.Gateway = (p.Gateway ?? string.Empty).Trim();
                p.PrimaryDns = (p.PrimaryDns ?? string.Empty).Trim();
                p.SecondaryDns = (p.SecondaryDns ?? string.Empty).Trim();
                p.VlanId = (p.VlanId ?? string.Empty).Trim();
                p.Description = (p.Description ?? string.Empty).Trim();

                IpMode ipMode;
                DnsMode dnsMode;
                if (!Enum.TryParse(p.IpConfig.ToString(), true, out ipMode)) ipMode = IpMode.Static;
                if (!Enum.TryParse(p.DnsConfig.ToString(), true, out dnsMode)) dnsMode = DnsMode.Static;
                p.IpConfig = ipMode;
                p.DnsConfig = dnsMode;

                string problem = p.Validate();
                if (problem != null)
                {
                    rejected.Add((p.Name.Length == 0 ? "(unnamed)" : p.Name) + ": " + problem);
                    continue;
                }

                // Importing twice must not silently create duplicates.
                p.Name = UniqueName(p.Name);
                _profiles.Add(p);
                added++;
            }

            if (added == 0 && error == null)
                error = "No valid profiles were found in the file.";

            return added;
        }

        /// <summary>Returns a name not already used, appending " (2)", " (3)" and so on.</summary>
        public static string UniqueName(string desired)
        {
            string baseName = string.IsNullOrEmpty(desired) ? "Profile" : desired;
            if (!Exists(baseName)) return baseName;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (!Exists(candidate)) return candidate;
            }

            return baseName + " (new)";
        }

        public static bool Exists(string name)
        {
            foreach (NetworkProfile p in _profiles)
            {
                if (string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)) return true;
            }
            return false;
        }

        public static int IndexOf(string name)
        {
            for (int i = 0; i < _profiles.Count; i++)
            {
                if (string.Equals(_profiles[i].Name, name, StringComparison.CurrentCultureIgnoreCase))
                    return i;
            }
            return -1;
        }

        public static void Add(NetworkProfile p)
        {
            p.Name = UniqueName(p.Name);
            _profiles.Add(p);
        }

        public static void RemoveAt(int index)
        {
            if (index >= 0 && index < _profiles.Count) _profiles.RemoveAt(index);
        }
    }
}