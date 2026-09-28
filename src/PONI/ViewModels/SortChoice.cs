using Poni.Core;
using Poni.Infrastructure;
using Poni.Services;

namespace Poni.ViewModels
{
    /// <summary>One entry of the funnel menu of the profiles list.</summary>
    public sealed class SortChoice
    {
        public SortChoice(ProfilesViewModel owner, ProfileSort sort)
        {
            Sort = sort;
            IsCurrent = owner.Sort == sort;
            Command = new RelayCommand(() => owner.Sort = sort);
        }

        public ProfileSort Sort { get; }
        public bool IsCurrent { get; }
        public string Label => LocalizationService.Get(KeyOf(Sort));
        public RelayCommand Command { get; }

        // Full keys spelled out: the strings test checks every key used in the sources exists.
        public static string KeyOf(ProfileSort sort) => sort switch
        {
            ProfileSort.NameAsc => "Str.Sort.NameAsc",
            ProfileSort.NameDesc => "Str.Sort.NameDesc",
            ProfileSort.CreatedAsc => "Str.Sort.CreatedAsc",
            ProfileSort.CreatedDesc => "Str.Sort.CreatedDesc",
            _ => "Str.Sort.Custom",
        };
    }
}
