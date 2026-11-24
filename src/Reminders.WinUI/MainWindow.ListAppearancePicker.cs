using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Reminders.Core;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private sealed class ListAppearancePicker
    {
        private const string Custom = "custom";
        private static readonly (string Label, string Badge)[] Symbols =
        [
            ("List", "default"), ("NewListBookmark", "bookmarks1"), ("NewListGift", "celebration1"),
            ("School", "education1"), ("NewListFinance", "finance1"), ("NewListHealth", "health1"),
            ("Home", "lifestyle2"), ("NewListLocation", "location1"), ("NewListMusic", "media2"), ("NewListNature", "nature1"),
            ("Shopping", "shopping2"), ("Travel", "vacation"), ("Work", "work1")
        ];
        private static readonly (string Label, string Glyph)[] Emojis =
        [
            ("List", "📝"), ("NewListBookmark", "🔖"), ("NewListGift", "🎁"), ("School", "🎓"),
            ("NewListFinance", "💰"), ("NewListFood", "🍽️"), ("NewListHealth", "🩺"), ("Home", "🏠"),
            ("NewListLocation", "📍"), ("NewListMusic", "🎵"), ("NewListNature", "🌿"), ("NewListPet", "🐾"),
            ("Shopping", "🛒"), ("Travel", "✈️"), ("Work", "💼"), ("Heart", "❤️"), ("Star", "⭐")
        ];
        private static readonly (string Label, string Hex)[] Palette =
        [
            ("Blue", "#007AFF"), ("Red", "#FF3B30"), ("Orange", "#FF9500"), ("NewListYellow", "#FFCC00"),
            ("Green", "#34C759"), ("NewListLightBlue", "#51AAF2"), ("NewListIndigo", "#5856D6"),
            ("Pink", "#EA426A"), ("Purple", "#C077DB"), ("NewListBrown", "#A2845E"),
            ("Gray", "#5B6770"), ("NewListRose", "#D9A69F")
        ];
        private static readonly (string Label, string Hex)[] OverridePalette =
        [
            ("Blue", "#0078D4"), ("Red", "#D13438"), ("Orange", "#CA5010"), ("Green", "#107C10"),
            ("Purple", "#8764B8"), ("Pink", "#C239B3"), ("Gray", "#767676")
        ];

        private readonly ReminderList? _list;
        private readonly ComboBox _color = new() { Header = L10n.Get("Color"), HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly RadioButtons _category = new()
        {
            Header = L10n.Get("Icon"), MaxColumns = 2,
            ItemsSource = new[] { L10n.Get("ListSFSymbols"), L10n.Get("ListEmoji") }
        };
        private readonly ComboBox _symbols = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _emojis = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _customSymbol = new()
        {
            Header = L10n.Get("CustomListSymbol"), PlaceholderText = L10n.Get("ListSymbolPlaceholder"), MaxLength = 64
        };
        private readonly TextBox _customEmoji = new() { Header = L10n.Get("CustomListEmoji"), MaxLength = 64 };
        private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SettingsDescriptionStyle"] };
        private readonly ContentControl _iconPreview = new() { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _namePreview = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        public ContentControl View { get; }
        public string Title { set => _namePreview.Text = value; }
        public event EventHandler? Changed;

        private bool UseEmoji => _category.SelectedIndex == 1;
        private bool UsesCustom => SelectedValue(UseEmoji ? _emojis : _symbols) == Custom;
        public bool IsValid => !UseEmoji || !UsesCustom || EmojiGlyph.IsValid(_customEmoji.Text.Trim());
        public string? Color => SelectedValue(_color);
        public string? Badge
        {
            get
            {
                if (!UsesCustom) return SelectedValue(UseEmoji ? _emojis : _symbols);
                if (!UseEmoji) return Nonempty(_customSymbol.Text.Trim());
                return IsValid ? EmojiBadge(_customEmoji.Text.Trim()) : null;
            }
        }

        public ListAppearancePicker(string title, ReminderList? list = null, ListAppearance? appearance = null)
        {
            _list = list;
            Title = title;
            AutomationProperties.SetName(_symbols, L10n.Get("ListSFSymbols"));
            AutomationProperties.SetName(_emojis, L10n.Get("ListEmoji"));
            if (list is not null)
            {
                _color.Items.Add(new ComboBoxItem { Content = L10n.Get("UseICloudListColor"), Tag = "" });
                _symbols.Items.Add(Choice(L10n.Get("UseICloudListIcon"), "", MakeSidebarIcon(list.BadgeEmblem, list.ColorHex)));
                _emojis.Items.Add(Choice(L10n.Get("UseICloudListIcon"), "", MakeSidebarIcon(list.BadgeEmblem, list.ColorHex)));
            }
            foreach (var (label, hex) in list is null ? Palette : OverridePalette) _color.Items.Add(ColorChoice(L10n.Get(label), hex));
            foreach (var (label, badge) in Symbols) _symbols.Items.Add(Choice(L10n.Get(label), badge, MakeSidebarIcon(badge, null)));
            foreach (var (label, glyph) in Emojis) _emojis.Items.Add(Choice(L10n.Get(label), EmojiBadge(glyph), MakeSidebarIcon(glyph, null)));
            if (list is not null)
                _symbols.Items.Add(new ComboBoxItem { Content = L10n.Get("CustomListSymbol"), Tag = Custom });
            _emojis.Items.Add(new ComboBoxItem { Content = L10n.Get("CustomListEmoji"), Tag = Custom });

            _color.SelectedIndex = _symbols.SelectedIndex = _emojis.SelectedIndex = 0;
            _category.SelectedIndex = 0;
            if (list is not null)
            {
                var storedColor = appearance?.Color;
                var colorIndex = FindChoice(_color, storedColor, StringComparison.OrdinalIgnoreCase);
                if (colorIndex < 0 && !string.IsNullOrWhiteSpace(storedColor))
                {
                    _color.Items.Add(ColorChoice(storedColor, storedColor));
                    colorIndex = _color.Items.Count - 1;
                }
                _color.SelectedIndex = Math.Max(0, colorIndex);

                var storedBadge = appearance?.Glyph;
                var resolved = SidebarPresentation.ResolveIcon(storedBadge ?? list.BadgeEmblem);
                _category.SelectedIndex = resolved.IsEmoji ? 1 : 0;
                var active = resolved.IsEmoji ? _emojis : _symbols;
                var alternate = resolved.IsEmoji ? _symbols : _emojis;
                alternate.SelectedIndex = 1;
                if (!string.IsNullOrWhiteSpace(storedBadge))
                {
                    var badgeIndex = FindChoice(active, resolved.IsEmoji ? EmojiBadge(resolved.Glyph) : storedBadge, StringComparison.Ordinal);
                    if (badgeIndex < 0)
                    {
                        active.SelectedIndex = active.Items.Count - 1;
                        if (resolved.IsEmoji) _customEmoji.Text = resolved.Glyph;
                        else _customSymbol.Text = storedBadge;
                    }
                    else active.SelectedIndex = badgeIndex;
                }
            }

            var preview = new Grid { ColumnSpacing = 12 };
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            preview.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(_namePreview, 1);
            preview.Children.Add(_iconPreview);
            preview.Children.Add(_namePreview);
            var form = new StackPanel { Spacing = 14 };
            foreach (var control in new UIElement[] { preview, _color, _category, _symbols, _emojis, _customSymbol, _customEmoji, _hint })
                form.Children.Add(control);
            View = new ContentControl { Content = form, HorizontalContentAlignment = HorizontalAlignment.Stretch };

            _color.SelectionChanged += (_, _) => Update();
            _category.SelectionChanged += (_, _) => Update();
            _symbols.SelectionChanged += (_, _) => Update();
            _emojis.SelectionChanged += (_, _) => Update();
            _customSymbol.TextChanged += (_, _) => Update();
            _customEmoji.TextChanged += (_, _) => Update();
            Update();
        }

        private void Update()
        {
            _symbols.Visibility = UseEmoji ? Visibility.Collapsed : Visibility.Visible;
            _emojis.Visibility = UseEmoji ? Visibility.Visible : Visibility.Collapsed;
            _customSymbol.Visibility = !UseEmoji && UsesCustom ? Visibility.Visible : Visibility.Collapsed;
            _customEmoji.Visibility = UseEmoji && UsesCustom ? Visibility.Visible : Visibility.Collapsed;
            var badge = Badge;
            _hint.Text = "";
            if (!IsValid && _customEmoji.Text.Length > 0) _hint.Text = L10n.Get("InvalidListEmoji");
            if (!UseEmoji && UsesCustom && badge is not null && SidebarPresentation.ResolveIcon(badge).IsFallback)
                _hint.Text = L10n.Get("ListSymbolFallback");
            _hint.Visibility = _hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _iconPreview.Content = MakeSidebarIcon(badge ?? _list?.BadgeEmblem, Color ?? _list?.ColorHex);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static string EmojiBadge(string glyph) => JsonSerializer.Serialize(new { Emoji = glyph });
        private static string? Nonempty(string? value) => value is { Length: > 0 } ? value : null;
        private static string? SelectedValue(ComboBox picker) => Nonempty((picker.SelectedItem as ComboBoxItem)?.Tag as string);
        private static int FindChoice(ComboBox picker, string? value, StringComparison comparison)
        {
            for (var i = 0; i < picker.Items.Count; i++)
                if (picker.Items[i] is ComboBoxItem { Tag: string tag } && tag != Custom && string.Equals(tag, value, comparison)) return i;
            return -1;
        }
        private static ComboBoxItem ColorChoice(string label, string hex) =>
            Choice(label, hex, new FontIcon { Glyph = "\uEA3B", Foreground = ColorBrush(hex), FontSize = 16 });
        private static ComboBoxItem Choice(string label, string value, IconElement icon)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            return new ComboBoxItem { Content = row, Tag = value };
        }
    }
}
