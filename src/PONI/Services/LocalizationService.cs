using System;
using System.Globalization;
using System.Windows;

namespace Poni.Services
{
    public enum AppLanguage { French, English }

    /// <summary>
    /// Swaps the string table (Strings/Strings.fr.xaml or Strings.en.xaml). UI text is bound
    /// with {DynamicResource Str.*}, so the whole interface switches language instantly.
    /// </summary>
    public static class LocalizationService
    {
        private static ResourceDictionary? _strings;

        public static AppLanguage Language { get; private set; }

        public static event EventHandler? LanguageChanged;

        public static void Initialize(AppLanguage language)
        {
            Language = language;
            Apply();
        }

        public static void SetLanguage(AppLanguage language)
        {
            if (language == Language) return;
            Language = language;
            Apply();
        }

        /// <summary>Looks up a string for code-behind use (messages built at runtime).</summary>
        public static string Get(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        /// <summary>Looks up a string and formats it with its arguments ({0}, {1}...).</summary>
        public static string Format(string key, params object?[] args)
        {
            var text = Get(key);
            if (args == null || args.Length == 0) return text;
            try { return string.Format(CultureInfo.CurrentCulture, text, args); }
            catch (FormatException) { return text; }
        }

        public static bool IsFrench => Language == AppLanguage.French;

        private static void Apply()
        {
            var code = Language == AppLanguage.French ? "fr" : "en";
            var strings = new ResourceDictionary
            {
                Source = new Uri($"/PONI;component/Strings/Strings.{code}.xaml", UriKind.Relative)
            };
            var merged = Application.Current.Resources.MergedDictionaries;
            if (_strings != null) merged.Remove(_strings);
            merged.Add(strings);
            _strings = strings;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
