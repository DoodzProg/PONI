using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Poni.Core
{
    /// <summary>Why an entry of a file was not kept (migration / import report).</summary>
    public sealed class SkippedEntry
    {
        public SkippedEntry(string? name, string reason)
        {
            Name = name;
            Reason = reason;
        }

        public string? Name { get; }
        /// <summary>A "Str.Val.*" key, or "Str.Val.NotAnObject".</summary>
        public string Reason { get; }

        public override string ToString() => (Name ?? "?") + ": " + Reason;
    }

    /// <summary>Profiles read from a file, with the entries that were rejected.</summary>
    public sealed class ProfileReadResult
    {
        public List<NetworkProfile> Profiles { get; } = new List<NetworkProfile>();
        public List<SkippedEntry> Skipped { get; } = new List<SkippedEntry>();
    }

    /// <summary>
    /// Converts between StoreData and JSON: the v2 format (store.json), the v1 format
    /// (profiles.json, read-only migration) and the export/import format.
    /// </summary>
    public static class StoreSerializer
    {
        private const string IsoDate = "yyyy-MM-dd'T'HH:mm:ss";

        // ============================================================ v2 (store.json)

        public static string Serialize(StoreData data)
        {
            var s = data.Settings;
            var root = new JsonObject
            {
                { "SchemaVersion", StoreData.CurrentSchemaVersion },
                { "Settings", new JsonObject
                    {
                        { "Language", s.Language },
                        { "Theme", s.Theme.ToString() },
                        { "ProfileSort", s.ProfileSort.ToString() },
                        { "HyperVEnabled", s.HyperVEnabled },
                        { "Rj45PhysicalAdapter", s.Rj45PhysicalAdapter },
                        { "SetPrivateOnApply", s.SetPrivateOnApply },
                        { "RollbackOnFailure", s.RollbackOnFailure },
                        { "VmAllowPing", s.VmAllowPing },
                        { "SidebarCollapsed", s.SidebarCollapsed },
                        { "DetailedView", s.DetailedView },
                        { "VmUsers", ToJsonObject(s.VmUsers) },
                    }
                },
                { "Profiles", data.Profiles.Select(p => (object?)ProfileToJson(p, includeHistory: true)).ToList() },
            };
            return Json.Write(root);
        }

        /// <summary>Reads a v2 store. Throws FormatException if the text is not a PONI v2 store.</summary>
        public static StoreData Deserialize(string json, out List<SkippedEntry> skipped)
        {
            var root = Json.AsObject(Json.Parse(json)) ?? throw new FormatException("The store is not a JSON object.");
            if (!Json.Has(root, "SchemaVersion") || Json.AsInt(Json.Get(root, "SchemaVersion")) == null)
                throw new FormatException("Missing SchemaVersion: not a PONI v2 store.");

            var data = new StoreData { Settings = ReadSettings(Json.AsObject(Json.Get(root, "Settings"))) };
            var read = ReadProfiles(Json.Get(root, "Profiles"), keepHistory: true);
            data.Profiles.AddRange(read.Profiles);
            skipped = read.Skipped;
            return data;
        }

        private static AppSettings ReadSettings(IDictionary<string, object?>? obj)
        {
            var s = new AppSettings();
            if (obj == null) return s;
            var lang = Json.AsString(Json.Get(obj, "Language"));
            s.Language = lang == "fr" || lang == "en" ? lang : null;
            s.Theme = ParseEnum(Json.Get(obj, "Theme"), ThemePreference.System);
            // "Accent" (a selectable colour in early v2 builds) is ignored: one colour only.
            s.ProfileSort = ParseEnum(Json.Get(obj, "ProfileSort"), ProfileSort.Custom);
            s.HyperVEnabled = Json.AsBool(Json.Get(obj, "HyperVEnabled"));
            s.Rj45PhysicalAdapter = Json.AsString(Json.Get(obj, "Rj45PhysicalAdapter"));
            s.SetPrivateOnApply = Json.AsBool(Json.Get(obj, "SetPrivateOnApply")) ?? false;
            s.RollbackOnFailure = Json.AsBool(Json.Get(obj, "RollbackOnFailure")) ?? true;
            s.VmAllowPing = Json.AsBool(Json.Get(obj, "VmAllowPing")) ?? true;
            s.SidebarCollapsed = Json.AsBool(Json.Get(obj, "SidebarCollapsed")) ?? false;
            s.DetailedView = Json.AsBool(Json.Get(obj, "DetailedView")) ?? false;
            var users = Json.AsObject(Json.Get(obj, "VmUsers"));
            if (users != null)
            {
                foreach (var pair in users)
                {
                    var user = Json.AsString(pair.Value);
                    if (pair.Key.Length > 0 && user != null) s.VmUsers[pair.Key] = user;
                }
            }
            return s;
        }

        private static JsonObject ToJsonObject(IDictionary<string, string> map)
        {
            var obj = new JsonObject();
            foreach (var pair in map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) obj.Add(pair.Key, pair.Value);
            return obj;
        }

        // ============================================================ v1 (profiles.json, read-only)

        /// <summary>
        /// Converts a v1 store (PONI 1.0 / NetManager, written by PowerShell ConvertTo-Json):
        /// { "Profiles": [...], "RJ45": { "PhysicalAdapter", "CurrentTarget" }, "Settings": { "Language" } }.
        /// Every profile goes through the same validation as the form; invalid ones are reported.
        /// </summary>
        public static StoreData FromV1(string json, out List<SkippedEntry> skipped)
        {
            var root = Json.AsObject(Json.Parse(json)) ?? throw new FormatException("The v1 store is not a JSON object.");
            var data = new StoreData();

            var lang = Json.AsString(Json.Get(Json.AsObject(Json.Get(root, "Settings")), "Language"));
            if (lang == "fr" || lang == "en") data.Settings.Language = lang;
            data.Settings.Rj45PhysicalAdapter = Json.AsString(Json.Get(Json.AsObject(Json.Get(root, "RJ45")), "PhysicalAdapter"));

            var read = ReadProfiles(Json.Get(root, "Profiles"), keepHistory: true);
            data.Profiles.AddRange(read.Profiles);
            skipped = read.Skipped;
            return data;
        }

        // ============================================================ export / import

        /// <summary>
        /// Export file: only the portable part of each profile (name, IP, prefix, gateway, DNS).
        /// No last target, VM or adapter names, no dates: nothing about this PC leaks.
        /// </summary>
        public static string Export(IEnumerable<NetworkProfile> profiles)
        {
            var root = new JsonObject
            {
                { "PONIProfiles", profiles.Select(p => (object?)ProfileToJson(p, includeHistory: false)).ToList() },
            };
            return Json.Write(root);
        }

        /// <summary>
        /// Reads profiles from an export (v1 or v2: { "PONIProfiles": [...] }), a whole store
        /// ({ "Profiles": [...] }) or a bare array. Invalid entries are skipped one by one, they
        /// never make the whole import fail. History fields are dropped.
        /// Throws FormatException only if the text is not JSON or holds no profile list.
        /// </summary>
        public static ProfileReadResult Import(string json)
        {
            var parsed = Json.Parse(json);
            var root = Json.AsObject(parsed);
            object? list;
            if (root != null && Json.Has(root, "PONIProfiles")) list = Json.Get(root, "PONIProfiles");
            else if (root != null && Json.Has(root, "Profiles")) list = Json.Get(root, "Profiles");
            else if (root == null && parsed is System.Collections.IEnumerable && !(parsed is string)) list = parsed;
            else throw new FormatException("No profile list in this file.");
            return ReadProfiles(list, keepHistory: false);
        }

        // ============================================================ profiles

        private static JsonObject ProfileToJson(NetworkProfile p, bool includeHistory)
        {
            var obj = new JsonObject
            {
                { "Name", p.Name },
                { "IPAddress", p.IPAddress },
                { "PrefixLength", p.PrefixLength },
                { "Gateway", string.IsNullOrEmpty(p.Gateway) ? null : p.Gateway },
                { "Dns", p.Dns.Cast<object?>().ToList() },
            };
            if (includeHistory)
            {
                obj.Add("CreatedOn", p.CreatedOn?.ToString(IsoDate, CultureInfo.InvariantCulture));
                obj.Add("LastTarget", p.LastTarget == null ? null : new JsonObject
                {
                    { "Kind", p.LastTarget.Kind.ToString() },
                    { "Adapter", p.LastTarget.Adapter },
                    { "VM", p.LastTarget.VM },
                    { "VMAdapter", p.LastTarget.VMAdapter },
                    { "AppliedOn", p.LastTarget.AppliedOn?.ToString(IsoDate, CultureInfo.InvariantCulture) },
                });
            }
            return obj;
        }

        private static ProfileReadResult ReadProfiles(object? value, bool keepHistory)
        {
            var result = new ProfileReadResult();
            var names = new List<string>();
            foreach (var item in Json.AsList(value))
            {
                var obj = Json.AsObject(item);
                if (obj == null)
                {
                    result.Skipped.Add(new SkippedEntry(null, "Str.Val.NotAnObject"));
                    continue;
                }

                var name = Json.AsString(Json.Get(obj, "Name"));
                var prefix = Json.AsInt(Json.Get(obj, "PrefixLength"));
                var input = new ProfileInput
                {
                    Name = name,
                    IPAddress = Json.AsString(Json.Get(obj, "IPAddress")),
                    // A missing or non-numeric PrefixLength must fail THIS entry only (v1 import bug).
                    Mask = prefix?.ToString(CultureInfo.InvariantCulture) ?? Json.AsString(Json.Get(obj, "SubnetMask")),
                    Gateway = Json.AsString(Json.Get(obj, "Gateway")),
                    // v2 writes "Dns", v1 wrote "DNS" (lookup is case-insensitive); array or text.
                    Dns = string.Join(",", Json.AsList(Json.Get(obj, "Dns")).Select(Json.AsString).Where(s => s != null)),
                };

                var check = ProfileValidator.Validate(input, names);
                if (!check.IsValid || check.Profile == null)
                {
                    result.Skipped.Add(new SkippedEntry(name, check.Errors.First().Key));
                    continue;
                }

                var profile = check.Profile;
                if (keepHistory)
                {
                    profile.CreatedOn = ParseDate(Json.Get(obj, "CreatedOn"));
                    profile.LastTarget = ReadLastTarget(obj);
                }
                names.Add(profile.Name);
                result.Profiles.Add(profile);
            }
            return result;
        }

        /// <summary>v2: nested "LastTarget" object. v1: flat LastTarget ("Host"/"Hote"/"VM") + LastAdapterName, LastVMName, LastVMAdapterName, LastAppliedOn.</summary>
        private static LastTarget? ReadLastTarget(IDictionary<string, object?> profile)
        {
            var raw = Json.Get(profile, "LastTarget");
            var nested = Json.AsObject(raw);
            if (nested != null)
            {
                var kind = ParseKind(Json.AsString(Json.Get(nested, "Kind")));
                if (kind == null) return null;
                return new LastTarget
                {
                    Kind = kind.Value,
                    Adapter = Json.AsString(Json.Get(nested, "Adapter")),
                    VM = Json.AsString(Json.Get(nested, "VM")),
                    VMAdapter = Json.AsString(Json.Get(nested, "VMAdapter")),
                    AppliedOn = ParseDate(Json.Get(nested, "AppliedOn")),
                };
            }

            var v1Kind = ParseKind(Json.AsString(raw));
            if (v1Kind == null) return null;
            var target = new LastTarget
            {
                Kind = v1Kind.Value,
                Adapter = Json.AsString(Json.Get(profile, "LastAdapterName")),
                VM = Json.AsString(Json.Get(profile, "LastVMName")),
                VMAdapter = Json.AsString(Json.Get(profile, "LastVMAdapterName")),
                AppliedOn = ParseDate(Json.Get(profile, "LastAppliedOn")),
            };
            // A v1 target without its adapter / VM carries no usable information.
            if (target.Kind == TargetKind.Host && target.Adapter == null) return null;
            if (target.Kind == TargetKind.VM && target.VM == null) return null;
            return target;
        }

        private static TargetKind? ParseKind(string? text)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "host":
                case "hote":
                case "hôte": return TargetKind.Host;
                case "vm": return TargetKind.VM;
                default: return null;
            }
        }

        /// <summary>v2 ISO dates, plus the two v1 formats ("2026-09-03 17:07" and "03/09/26-17:07:21").</summary>
        private static DateTime? ParseDate(object? value)
        {
            var text = Json.AsString(value);
            if (text == null) return null;
            string[] formats = { IsoDate, "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd/MM/yy-HH:mm:ss", "dd/MM/yyyy HH:mm:ss" };
            return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : (DateTime?)null;
        }

        private static T ParseEnum<T>(object? value, T fallback) where T : struct
        {
            var text = Json.AsString(value);
            return text != null && Enum.TryParse<T>(text, true, out var parsed) && Enum.IsDefined(typeof(T), parsed) ? parsed : fallback;
        }
    }
}
