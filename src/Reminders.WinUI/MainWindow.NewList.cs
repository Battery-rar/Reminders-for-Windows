using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Reminders.Windows.Services;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private const int ListNameByteLimit = 255;
    private static readonly (string Label, string Badge)[] NewListSymbols =
    [
        ("List", "default"), ("NewListBookmark", "bookmarks1"), ("NewListGift", "celebration1"),
        ("School", "education1"), ("NewListFinance", "finance1"), ("NewListHealth", "health1"),
        ("Home", "lifestyle2"), ("NewListLocation", "location1"), ("NewListMusic", "media2"), ("NewListNature", "nature1"),
        ("Shopping", "shopping2"), ("Travel", "vacation"), ("Work", "work1")
    ];
    private static readonly (string Label, string Glyph)[] NewListEmoji =
    [
        ("List", "📝"), ("NewListBookmark", "🔖"), ("NewListGift", "🎁"), ("School", "🎓"),
        ("NewListFinance", "💰"), ("NewListFood", "🍽️"), ("NewListHealth", "🩺"), ("Home", "🏠"),
        ("NewListLocation", "📍"), ("NewListMusic", "🎵"), ("NewListNature", "🌿"), ("NewListPet", "🐾"),
        ("Shopping", "🛒"), ("Travel", "✈️"), ("Work", "💼"), ("Heart", "❤️"), ("Star", "⭐")
    ];
    private static readonly (string Label, string Hex)[] NewListColors =
    [
        ("Blue", "#007AFF"), ("Red", "#FF3B30"), ("Orange", "#FF9500"), ("NewListYellow", "#FFCC00"),
        ("Green", "#34C759"), ("NewListLightBlue", "#51AAF2"), ("NewListIndigo", "#5856D6"),
        ("Pink", "#EA426A"), ("Purple", "#C077DB"), ("NewListBrown", "#A2845E"),
        ("Gray", "#5B6770"), ("NewListRose", "#D9A69F")
    ];

    private async Task ShowNewListAsync()
    {
        if (_dialogOpen || _accountBusy || _savingDetail || AuthGate.Visibility != Visibility.Collapsed
            || SetupGuide.Visibility == Visibility.Visible || !await ConfirmLeaveDetailAsync()) return;

        var accountEpoch = _accountEpoch;
        _dialogOpen = true;
        ReminderList? created = null;
        try
        {
            var name = new TextBox { Header = L10n.Get("NewListName"), MaxLength = ListNameByteLimit };
            var colors = new ComboBox { Header = L10n.Get("Color"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (label, hex) in NewListColors)
                colors.Items.Add(ListChoice(label, hex, new FontIcon { Glyph = "\uEA3B", Foreground = ColorBrush(hex), FontSize = 16 }));
            var symbolsLabel = L10n.Get("NewListWindowsSymbols");
            var emojiLabel = L10n.Get("NewListEmoji");
            var iconKind = new RadioButtons { Header = L10n.Get("Icon"), MaxColumns = 2, ItemsSource = new[] { symbolsLabel, emojiLabel }, SelectedIndex = 0 };
            var symbols = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var emoji = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(symbols, symbolsLabel);
            AutomationProperties.SetName(emoji, emojiLabel);
            foreach (var (label, badge) in NewListSymbols)
                symbols.Items.Add(ListChoice(label, badge, MakeSidebarIcon(badge, null)));
            foreach (var (label, glyph) in NewListEmoji)
                emoji.Items.Add(ListChoice(label, JsonSerializer.Serialize(new { Emoji = glyph }), MakeSidebarIcon(glyph, null)));
            colors.SelectedIndex = symbols.SelectedIndex = emoji.SelectedIndex = 0;

            var iconPreview = new ContentControl { VerticalContentAlignment = VerticalAlignment.Center };
            var namePreview = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var preview = new Grid { ColumnSpacing = 12 };
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            preview.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(namePreview, 1);
            preview.Children.Add(iconPreview);
            preview.Children.Add(namePreview);

            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Visibility = Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(preview);
            panel.Children.Add(name);
            panel.Children.Add(colors);
            panel.Children.Add(iconKind);
            panel.Children.Add(symbols);
            panel.Children.Add(emoji);
            panel.Children.Add(error);
            var dialog = MakeDialog(L10n.Get("NewList"), panel, L10n.Get("CreateList"));
            var requestId = Guid.NewGuid().ToString("D");
            var creating = false;

            string Color() => ((ComboBoxItem)colors.SelectedItem).Tag!.ToString()!;
            bool UseEmoji() => iconKind.SelectedIndex == 1;
            string Badge() => ((ComboBoxItem)(UseEmoji() ? emoji : symbols).SelectedItem).Tag!.ToString()!;
            void UpdateForm()
            {
                var title = name.Text.Trim();
                var tooLong = Encoding.UTF8.GetByteCount(title) > ListNameByteLimit;
                dialog.IsPrimaryButtonEnabled = !creating && title.Length > 0 && !tooLong;
                iconPreview.Content = MakeSidebarIcon(Badge(), Color());
                namePreview.Text = title.Length == 0 ? L10n.Get("NewList") : title;
                if (!creating)
                {
                    error.Message = tooLong ? L10n.Get("NewListNameTooLong") : "";
                    error.IsOpen = tooLong;
                    error.Visibility = tooLong ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            name.TextChanged += (_, _) => UpdateForm();
            colors.SelectionChanged += (_, _) => UpdateForm();
            symbols.SelectionChanged += (_, _) => UpdateForm();
            emoji.SelectionChanged += (_, _) => UpdateForm();
            iconKind.SelectionChanged += (_, _) =>
            {
                symbols.Visibility = UseEmoji() ? Visibility.Collapsed : Visibility.Visible;
                emoji.Visibility = UseEmoji() ? Visibility.Visible : Visibility.Collapsed;
                UpdateForm();
            };
            dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
            dialog.Closing += (_, args) => { if (creating) args.Cancel = true; };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                if (creating || !dialog.IsPrimaryButtonEnabled) { args.Cancel = true; return; }
                var deferral = args.GetDeferral();
                creating = true;
                name.IsEnabled = colors.IsEnabled = iconKind.IsEnabled = symbols.IsEnabled = emoji.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    var title = name.Text.Trim();
                    var color = Color();
                    var badge = Badge();
                    created = _demo
                        ? new ReminderList { Id = "preview-list/" + requestId, Title = title, ColorHex = color, BadgeEmblem = badge }
                        : await _sync.CallAsync<ReminderList>("create_list", new
                        {
                            title, color_hex = color, badge_emblem = badge, request_id = requestId
                        });
                }
                catch (Exception failure)
                {
                    args.Cancel = true;
                    error.Message = L10n.Error(failure);
                    error.IsOpen = true;
                    error.Visibility = Visibility.Visible;
                }
                finally
                {
                    creating = false;
                    name.IsEnabled = colors.IsEnabled = iconKind.IsEnabled = symbols.IsEnabled = emoji.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };
            UpdateForm();
            await dialog.ShowAsync();
        }
        catch (Exception failure)
        {
            ShowInfo(L10n.Get("CouldntCreateList"), L10n.Error(failure), InfoBarSeverity.Error);
        }
        finally { _dialogOpen = false; }

        if (created is null || _windowClosed || _accountBusy || accountEpoch != _accountEpoch
            || AuthGate.Visibility != Visibility.Collapsed) return;
        _navigationLoads.Invalidate();
        _reminderLoads.Invalidate();
        _lists.RemoveAll(list => list.Id == created.Id);
        _lists.Add(created);
        _view = new(created.Title, "", NavKind.List, created.Id);
        ViewTitle.Text = created.Title;
        ViewSubtitle.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        ShowDetail(null);
        RebuildListNavigation(created.Id);
        await LoadRemindersAsync();
    }

    private static ComboBoxItem ListChoice(string label, string value, IconElement icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = L10n.Get(label), VerticalAlignment = VerticalAlignment.Center });
        return new ComboBoxItem { Content = row, Tag = value };
    }
}
