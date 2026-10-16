
using System;
using System.IO;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace AXamlIDE.ViewModels
{
    public sealed class ThemeViewModel : ObservableObject
    {
        private readonly Func<RegistryOptions, IRawTheme> _load;

        private ThemeViewModel(string id, string displayName, Func<RegistryOptions, IRawTheme> load)
        {
            Id = id;
            DisplayName = displayName;
            _load = load;
        }

        public string Id { get; }
        public string DisplayName { get; }

        public IRawTheme Load(RegistryOptions options) => _load(options);

        public static ThemeViewModel BuiltIn(ThemeName name) =>
            new(name.ToString(), name.ToString(), r => r.LoadTheme(name));

        public static ThemeViewModel Custom(string id, string displayName, string avaresUri) =>
            new(id, displayName, _ =>
            {
                using var stream = AssetLoader.Open(new Uri(avaresUri));
                using var reader = new StreamReader(stream);
                return ThemeReader.ReadThemeSync(reader);
            });
    }
}