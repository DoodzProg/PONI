using System;
using System.Collections.Generic;
using System.Linq;

namespace Poni.Core
{
    /// <summary>
    /// Order of the profiles list. Sorting is for display only: the stored list keeps the
    /// user's custom order, which only Move changes (drag and drop, "Move up / down").
    /// </summary>
    public static class ProfileOrder
    {
        public static List<NetworkProfile> Apply(IEnumerable<NetworkProfile> profiles, ProfileSort sort)
        {
            var list = profiles.ToList();
            var byName = StringComparer.CurrentCultureIgnoreCase;
            switch (sort)
            {
                case ProfileSort.NameAsc:
                    return list.OrderBy(p => p.Name, byName).ToList();
                case ProfileSort.NameDesc:
                    return list.OrderByDescending(p => p.Name, byName).ToList();
                case ProfileSort.CreatedAsc:
                    // No creation date (profiles from v1): last, by name.
                    return list.OrderBy(p => p.CreatedOn == null).ThenBy(p => p.CreatedOn).ThenBy(p => p.Name, byName).ToList();
                case ProfileSort.CreatedDesc:
                    return list.OrderBy(p => p.CreatedOn == null).ThenByDescending(p => p.CreatedOn).ThenBy(p => p.Name, byName).ToList();
                default:
                    return list;
            }
        }

        /// <summary>
        /// Custom order: puts <paramref name="moved"/> just above (or below) <paramref name="target"/>.
        /// Returns false when nothing changed.
        /// </summary>
        public static bool Move(IList<NetworkProfile> profiles, NetworkProfile moved, NetworkProfile target, bool below)
        {
            if (ReferenceEquals(moved, target)) return false;
            var from = profiles.IndexOf(moved);
            if (from < 0 || profiles.IndexOf(target) < 0) return false;
            profiles.RemoveAt(from);
            var to = profiles.IndexOf(target) + (below ? 1 : 0);
            profiles.Insert(to, moved);
            return to != from;
        }

        /// <summary>"Move up" (-1) / "Move down" (+1), keyboard alternative to dragging.</summary>
        public static bool Step(IList<NetworkProfile> profiles, NetworkProfile moved, int delta)
        {
            var from = profiles.IndexOf(moved);
            var to = from + delta;
            if (from < 0 || to < 0 || to >= profiles.Count) return false;
            profiles.RemoveAt(from);
            profiles.Insert(to, moved);
            return true;
        }
    }
}
