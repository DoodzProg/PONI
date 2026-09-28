using System;
using System.Collections.Generic;
using System.Linq;

namespace Poni.Core
{
    public sealed class MergeResult
    {
        public int Added { get; set; }
        public List<SkippedEntry> Skipped { get; } = new List<SkippedEntry>();
    }

    public static class ProfileMerge
    {
        /// <summary>
        /// Adds imported profiles to the store. A name already in use is never overwritten:
        /// the incoming profile is skipped and reported (same behaviour as v1).
        /// </summary>
        public static MergeResult AddImported(StoreData data, IEnumerable<NetworkProfile> incoming)
        {
            var result = new MergeResult();
            foreach (var profile in incoming)
            {
                if (data.Profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Skipped.Add(new SkippedEntry(profile.Name, "Str.Val.NameExists"));
                    continue;
                }
                var copy = profile.Clone();
                copy.LastTarget = null;
                copy.CreatedOn = DateTime.Now;
                data.Profiles.Add(copy);
                result.Added++;
            }
            return result;
        }
    }
}
