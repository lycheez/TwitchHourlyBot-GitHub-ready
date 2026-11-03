using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TwitchHourlyBot;

public partial class EventSettingsWindow : Window
{
    private readonly ChannelConfig _channel;
    private readonly EventEngineSettings _global;
    private readonly ObservableCollection<string> _messageItems = new();
    private readonly Dictionary<string, CheckBox> _categoryChecks = new();
    private readonly Dictionary<string, TextBox> _categoryCooldowns = new();
    private readonly Dictionary<string, string> _categoryLabels = new();

    public EventSettingsWindow(ChannelConfig channel, EventEngineSettings global)
    {
        InitializeComponent();
        _channel = channel;
        _global = global;

        TitleText.Text = $"Event Settings • {channel.Channel}";
        ChannelNameText.Text = channel.Channel;
        GlobalEnabledBox.IsChecked = global.Enabled;
        GlobalIntervalBox.Text = Math.Max(5, global.IntervalMinutes).ToString();
        ChannelEnabledBox.IsChecked = channel.EventSettings.Enabled;

        CategoryBox.ItemsSource = EventCategories.All.Select(EventCategories.DisplayName).ToList();
        CategoryBox.SelectedIndex = 0;
        MessageList.ItemsSource = _messageItems;

        BuildCategoryRows();
        RefreshMessages();
    }

    private void BuildCategoryRows()
    {
        CategoryPanel.Children.Clear();
        _categoryChecks.Clear();
        _categoryCooldowns.Clear();

        foreach (var category in EventCategories.All)
        {
            var setting = _channel.EventSettings.GetCategory(category);
            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 31, 35)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 50)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            var stack = new StackPanel();
            var check = new CheckBox
            {
                Content = EventCategories.DisplayName(category),
                IsChecked = setting.Enabled,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(check);
            var hint = new TextBlock
            {
                Text = category switch
                {
                    EventCategories.Hydration => "น้ำ / เครื่องดื่ม",
                    EventCategories.Eyes => "พักสายตา",
                    EventCategories.Stretch => "มือ / แขน / ไหล่",
                    EventCategories.Relax => "หายใจ / ผ่อนคลาย",
                    EventCategories.Encouragement => "กำลังใจแบบเบา ๆ",
                    _ => ""
                },
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(167, 167, 173)),
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0)
            };
            stack.Children.Add(hint);
            Grid.SetColumn(stack, 0);

            var cooldownStack = new StackPanel();
            cooldownStack.Children.Add(new TextBlock { Text = "Cooldown", FontSize = 10, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(167, 167, 173)) });
            var cooldown = new TextBox { Text = Math.Max(0, setting.CooldownMinutes).ToString(), Height = 30, Margin = new Thickness(0, 3, 0, 0) };
            cooldownStack.Children.Add(cooldown);
            Grid.SetColumn(cooldownStack, 1);

            grid.Children.Add(stack);
            grid.Children.Add(cooldownStack);
            border.Child = grid;
            CategoryPanel.Children.Add(border);

            _categoryChecks[category] = check;
            _categoryCooldowns[category] = cooldown;
        }
    }

    private void RefreshMessages()
    {
        _messageItems.Clear();
        foreach (var message in _global.Messages)
            _messageItems.Add($"{EventCategories.DisplayName(message.Category)}  •  {message.Text}");
        MessageCountText.Text = $"ทั้งหมด {_global.Messages.Count} ข้อความ • {EventCategories.All.Length} หมวด";
    }

    private void AddMessage_Click(object sender, RoutedEventArgs e)
    {
        var text = NewMessageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var category = EventCategories.All[CategoryBox.SelectedIndex < 0 ? 0 : CategoryBox.SelectedIndex];
        _global.Messages.Add(new EventMessage { Category = category, Text = text });
        NewMessageBox.Clear();
        RefreshMessages();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _global.Enabled = GlobalEnabledBox.IsChecked == true;
        _global.IntervalMinutes = ParseInt(GlobalIntervalBox.Text, 30, 5, 1440);
        _channel.EventSettings.Enabled = ChannelEnabledBox.IsChecked == true;

        foreach (var category in EventCategories.All)
        {
            var setting = _channel.EventSettings.GetCategory(category);
            setting.Enabled = _categoryChecks[category].IsChecked == true;
            setting.CooldownMinutes = ParseInt(_categoryCooldowns[category].Text, 60, 0, 10080);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, out var value)) return fallback;
        return Math.Clamp(value, min, max);
    }
}
