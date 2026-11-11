using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace vkr.Themes
{
    public static class ThemeManager
    {
        private const string ThemeFileName = "theme.txt";
        private static readonly Uri DarkThemeUri =
            new("pack://application:,,,/Resources/ThemeDark.xaml", UriKind.Absolute);

        public static bool IsDark { get; private set; }

        public static event EventHandler? ThemeChanged;

        public static void Initialize()
        {
            var dark = false;

            try
            {
                var path = PreferencePath;
                if (File.Exists(path))
                {
                    dark = string.Equals(File.ReadAllText(path).Trim(), "dark", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                dark = false;
            }

            Apply(dark, persist: false);
        }

        public static void SetTheme(bool dark) => Apply(dark, persist: true);

        private static void Apply(bool dark, bool persist)
        {
            var app = Application.Current;
            if (app is null)
            {
                return;
            }

            var darkDictionary = app.Resources.MergedDictionaries
                .FirstOrDefault(dictionary =>
                    dictionary.Source is not null &&
                    dictionary.Source.OriginalString.EndsWith("ThemeDark.xaml", StringComparison.OrdinalIgnoreCase));

            if (dark && darkDictionary is null)
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DarkThemeUri });
            }
            else if (!dark && darkDictionary is not null)
            {
                app.Resources.MergedDictionaries.Remove(darkDictionary);
            }

            IsDark = dark;

            if (persist)
            {
                SavePreference(dark);
            }

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static string PreferencePath
        {
            get
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SONORA");

                Directory.CreateDirectory(directory);
                return Path.Combine(directory, ThemeFileName);
            }
        }

        private static void SavePreference(bool dark)
        {
            try
            {
                File.WriteAllText(PreferencePath, dark ? "dark" : "light");
            }
            catch
            {
            }
        }
    }
}
