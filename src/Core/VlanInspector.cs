using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VlanConfig95.Core
{
    /// <summary>One advanced adapter property as reported by the driver.</summary>
    public sealed class VlanProperty
    {
        public string RegistryKeyword;
        public string DisplayName;
        public string DisplayValue;
        public string RegistryValue;
        public string ValueType;
        public string DisplayParameterType;
        public string ValidRegistryValues;
        public string ValidDisplayValues;
        public string NumericMin;
        public string NumericMax;
        public string DefaultRegistryValue;
        public string DefaultDisplayValue;

        public VlanProperty()
        {
            RegistryKeyword = string.Empty;
            DisplayName = string.Empty;
            DisplayValue = string.Empty;
            RegistryValue = string.Empty;
            ValueType = string.Empty;
            DisplayParameterType = string.Empty;
            ValidRegistryValues = string.Empty;
            ValidDisplayValues = string.Empty;
            NumericMin = string.Empty;
            NumericMax = string.Empty;
            DefaultRegistryValue = string.Empty;
            DefaultDisplayValue = string.Empty;
        }

        /// <summary>True when the driver models this as a free numeric field.</summary>
        public bool IsNumeric
        {
            get { return DisplayParameterType == "1"; }
        }

        /// <summary>True when the driver models this as a fixed choice list.</summary>
        public bool IsEnum
        {
            get { return DisplayParameterType == "5"; }
        }

        public override string ToString()
        {
            return RegistryKeyword + " (" + DisplayName + ")";
        }
    }

    /// <summary>Result of inspecting one adapter for VLAN support.</summary>
    public sealed class VlanSupport
    {
        public VlanProperty Property;
        public List<VlanProperty> Candidates = new List<VlanProperty>();
        public List<string> Rejected = new List<string>();
        public int? CurrentVlanId;
        public string CurrentValueText;
        public int? CimVlanId;

        public bool Supported
        {
            get { return Property != null; }
        }

        public VlanSupport()
        {
            CurrentValueText = string.Empty;
        }
    }

    /// <summary>
    /// Decides whether a driver really exposes a configurable 802.1Q VLAN ID.
    ///
    /// The rules were derived from real driver behaviour and deliberately reject
    /// look-alike properties:
    ///   * "*PriorityVLANTag" - an enum that only enables/disables tagging (Intel).
    ///   * "VlanFiltering"    - a filtering toggle, not an ID (Intel).
    ///   * "IntelANSVlanID"   - a team/ANS internal identifier, not user-facing.
    ///   * "*VlanEnable"      - a boolean switch with no ID range.
    ///
    /// A property qualifies only if its RegistryKeyword mentions VLAN *and* it is
    /// numeric with a range that can express 1..4094, or an enum whose valid values
    /// span a VLAN-like range.
    /// </summary>
    public static class VlanInspector
    {
        public const int MinVlanId = 1;
        public const int MaxVlanId = 4094;

        private static readonly Regex VlanWord = new Regex(
            @"(802[._]?1q|vlan)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // VLAN-matching keys that are definitely not a VLAN ID field.
        private static readonly string[] RejectedKeywords =
        {
            "priorityvlantag", "vlanfiltering", "priorityvlan", "vlantagging",
            "vlanenable", "vlanenabled", "vlanmode", "vlanpriority", "vlanfilter",
            "ansvlanid", "vlanqos", "vlantrafficclass", "vlanwindow"
        };

        public static VlanProperty FromRecord(Record r)
        {
            VlanProperty p = new VlanProperty();
            p.RegistryKeyword = r.Get(0);
            p.DisplayName = r.Get(1);
            p.DisplayValue = r.Get(2);
            p.RegistryValue = r.Get(3);
            p.ValueType = r.Get(4);
            p.DisplayParameterType = r.Get(5);
            p.ValidRegistryValues = r.Get(6);
            p.ValidDisplayValues = r.Get(7);
            p.NumericMin = r.Get(8);
            p.NumericMax = r.Get(9);
            p.DefaultRegistryValue = r.Get(10);
            p.DefaultDisplayValue = r.Get(11);
            return p;
        }

        /// <summary>Scores a property; higher means a better VLAN ID candidate.</summary>
        public static int Score(VlanProperty p)
        {
            if (p == null || string.IsNullOrEmpty(p.RegistryKeyword))
                return 0;

            string kw = p.RegistryKeyword.TrimStart('*').ToLowerInvariant();

            foreach (string bad in RejectedKeywords)
            {
                if (kw.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0)
                    return 0;
            }

            if (!VlanWord.IsMatch(kw))
                return 0;

            // Must be an ID field: numeric with a usable range.
            if (p.IsNumeric)
            {
                int min, max;
                if (TryInt(p.NumericMin, out min) && TryInt(p.NumericMax, out max))
                {
                    if (max < MinVlanId)
                        return 0;
                    if (min > MaxVlanId)
                        return 0;

                    int score = 50;
                    if (kw.Contains("vlanid") || kw.Contains("8021qid"))
                        score += 40;
                    else if (kw == "vlan" || kw == "8021qvlan")
                        score += 30;
                    else if (kw.Contains("vlan"))
                        score += 10;
                    return score;
                }

                // Numeric with no advertised range: plausible but lower confidence.
                return 20;
            }

            // Enum: accept only when valid registry values span a VLAN-like range.
            if (p.IsEnum)
            {
                List<int> values = ParseIntList(p.ValidRegistryValues);
                if (values.Count >= 2)
                {
                    int max = values[values.Count - 1];
                    if (max >= MaxVlanId)
                        return 40;
                    if (max >= 100)
                        return 25;
                }
            }

            return 0;
        }

        public static bool TryInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            return int.TryParse(text.Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Parses a comma-separated list of integers as emitted by PowerShell.</summary>
        public static List<int> ParseIntList(string text)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrEmpty(text))
                return result;

            string[] parts = text.Split(',');
            foreach (string raw in parts)
            {
                int v;
                if (TryInt(raw, out v))
                    result.Add(v);
            }

            result.Sort();
            return result;
        }

        /// <summary>
        /// Picks the best VLAN ID property and reads the current value.
        /// Returns a VlanSupport whose Supported flag reflects genuine driver support.
        /// </summary>
        public static VlanSupport Inspect(RecordSet set)
        {
            VlanSupport support = new VlanSupport();
            if (set == null)
                return support;

            VlanProperty best = null;
            int bestScore = 0;

            foreach (Record r in set.OfKind("P"))
            {
                VlanProperty p = FromRecord(r);
                int score = Score(p);

                if (score > 0)
                {
                    support.Candidates.Add(p);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = p;
                    }
                }
                else if (VlanWord.IsMatch((p.RegistryKeyword ?? string.Empty).TrimStart('*')))
                {
                    support.Rejected.Add(p.RegistryKeyword + "  [" +
                        (string.IsNullOrEmpty(p.DisplayName) ? "(no display name)" : p.DisplayName) +
                        ", type=" + (p.DisplayParameterType == string.Empty ? "n/a" : p.DisplayParameterType) + "]");
                }
            }

            // Native CIM VlanID (used as corroboration, and as the value when the
            // driver reports no settable property at all).
            foreach (Record r in set.OfKind("C"))
            {
                int cimVlan;
                if (TryInt(r.Get(0), out cimVlan))
                    support.CimVlanId = cimVlan;
            }

            support.Property = best;

            if (best != null)
            {
                int current;
                if (TryInt(best.RegistryValue, out current))
                {
                    support.CurrentValueText = best.RegistryValue;
                    if (current >= MinVlanId && current <= MaxVlanId)
                        support.CurrentVlanId = current;
                }
                else if (string.IsNullOrEmpty(best.RegistryValue))
                {
                    // Unset means "no VLAN / priority-tagged only".
                    support.CurrentValueText = string.Empty;
                }
                else
                {
                    support.CurrentValueText = best.RegistryValue;
                }

                // Prefer the driver's own value, but surface a mismatch with CIM.
                if (!support.CurrentVlanId.HasValue && support.CimVlanId.HasValue &&
                    support.CimVlanId.Value >= MinVlanId && support.CimVlanId.Value <= MaxVlanId)
                {
                    support.CurrentVlanId = support.CimVlanId;
                    support.CurrentValueText = support.CimVlanId.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (support.CimVlanId.HasValue &&
                     support.CimVlanId.Value >= MinVlanId &&
                     support.CimVlanId.Value <= MaxVlanId)
            {
                // A VLAN is active but we cannot change it: report support=false so the
                // UI disables editing rather than pretending the change will work.
                support.CurrentVlanId = support.CimVlanId;
                support.CurrentValueText = support.CimVlanId.Value.ToString(CultureInfo.InvariantCulture);
            }

            return support;
        }

        /// <summary>Validates a user-entered VLAN ID. Returns an error message or null.</summary>
        public static string ValidateId(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
                return "Enter a VLAN ID.";

            string trimmed = text.Trim();

            int value;
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return "The VLAN ID must be a whole number.";

            if (value < MinVlanId || value > MaxVlanId)
                return "The VLAN ID must be between " + MinVlanId + " and " + MaxVlanId + ".";

            return null;
        }
    }
}