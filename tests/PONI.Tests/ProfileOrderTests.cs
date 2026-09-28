using System;
using System.Collections.Generic;
using System.Linq;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    /// <summary>Sorting and custom order of the profiles list.</summary>
    public class ProfileOrderTests
    {
        private static NetworkProfile P(string name, int? day = null) =>
            new NetworkProfile { Name = name, CreatedOn = day == null ? (DateTime?)null : new DateTime(2026, 9, day.Value) };

        private static List<NetworkProfile> Sample() => new List<NetworkProfile>
        {
            P("bureau", 10), P("Atelier", 20), P("v1 import"), P("Client", 5),
        };

        private static string[] Names(IEnumerable<NetworkProfile> list) => list.Select(p => p.Name).ToArray();

        [Fact]
        public void Custom_keeps_the_stored_order()
        {
            Assert.Equal(new[] { "bureau", "Atelier", "v1 import", "Client" }, Names(ProfileOrder.Apply(Sample(), ProfileSort.Custom)));
        }

        [Fact]
        public void Names_are_sorted_without_caring_about_case()
        {
            Assert.Equal(new[] { "Atelier", "bureau", "Client", "v1 import" }, Names(ProfileOrder.Apply(Sample(), ProfileSort.NameAsc)));
            Assert.Equal(new[] { "v1 import", "Client", "bureau", "Atelier" }, Names(ProfileOrder.Apply(Sample(), ProfileSort.NameDesc)));
        }

        [Fact]
        public void Creation_date_puts_undated_v1_profiles_last()
        {
            Assert.Equal(new[] { "Client", "bureau", "Atelier", "v1 import" }, Names(ProfileOrder.Apply(Sample(), ProfileSort.CreatedAsc)));
            Assert.Equal(new[] { "Atelier", "bureau", "Client", "v1 import" }, Names(ProfileOrder.Apply(Sample(), ProfileSort.CreatedDesc)));
        }

        [Fact]
        public void Sorting_never_changes_the_stored_list()
        {
            var stored = Sample();
            ProfileOrder.Apply(stored, ProfileSort.NameAsc);
            Assert.Equal(new[] { "bureau", "Atelier", "v1 import", "Client" }, Names(stored));
        }

        [Fact]
        public void Dropping_above_or_below_another_profile()
        {
            var list = Sample();
            Assert.True(ProfileOrder.Move(list, list[3], list[0], below: false));   // Client above bureau
            Assert.Equal(new[] { "Client", "bureau", "Atelier", "v1 import" }, Names(list));
            Assert.True(ProfileOrder.Move(list, list[0], list[3], below: true));    // Client below v1 import
            Assert.Equal(new[] { "bureau", "Atelier", "v1 import", "Client" }, Names(list));
            Assert.False(ProfileOrder.Move(list, list[1], list[1], below: true));   // onto itself
            Assert.False(ProfileOrder.Move(list, list[1], list[0], below: true));   // already just below
            Assert.Equal(new[] { "bureau", "Atelier", "v1 import", "Client" }, Names(list));
        }

        [Fact]
        public void Move_up_and_down_stop_at_the_ends()
        {
            var list = Sample();
            Assert.False(ProfileOrder.Step(list, list[0], -1));
            Assert.True(ProfileOrder.Step(list, list[0], +1));
            Assert.Equal(new[] { "Atelier", "bureau", "v1 import", "Client" }, Names(list));
            Assert.False(ProfileOrder.Step(list, list[3], +1));
        }
    }
}
