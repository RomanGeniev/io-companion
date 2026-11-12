using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Controls;

namespace GraphiteCompanion;

public partial class MainWindow
{
    private readonly Dictionary<string, List<ViewLayoutGroup>> viewGroups = new();
    private string layoutPage = "Eq";
    private readonly Dictionary<string, SnapBoard> layoutBoards = new();
    private void InitViewLayout()
    {
        void Add(string page, string key, string title, Panel host, params string[] names)
        {
            var group = new ViewLayoutGroup(page + "." + key, title, host, names);
            if (!viewGroups.TryGetValue(page, out var list)) viewGroups[page] = list = new(); list.Add(group);
            settings.ViewBlocks ??= new(); settings.ViewBlocks.TryGetValue(group.Id, out var saved); group.Apply(saved);
        }
        var presetGrid = (Grid)PresetScroll.Content;
        Add("Eq", "columns", "Колонки", PageEq, "Режимы и эквалайзер", "Пресеты и амбушюры");
        Add("Eq", "editor", "Звук", EqEditor, "Режимы", "Эквалайзер");
        Add("Eq", "presets", "Пресеты", presetGrid, "Мои пресеты", "Амбушюры");
        var micRoot = (StackPanel)PageMic.Content;
        var micColumns = (Grid)micRoot.Children[1]; var micProcessing = (StackPanel)micColumns.Children[0];
        Add("Mic", "page", "На странице", micRoot, "Устройство и громкость", "Обработка и проверка голоса");
        Add("Mic", "columns", "Колонки", micColumns, "Обработка микрофона", "Проверка голоса");
        Add("Mic", "processing", "Обработка", micProcessing, "Шумоподавление", "Нажми и говори", "Эффекты микрофона");
        var recordRoot = (StackPanel)PageRecord.Content;
        var options = (Grid)recordRoot.Children[0];
        var recordChildren = recordRoot.Children.Cast<UIElement>().ToArray(); recordRoot.Children.Clear();
        // Status and error messages remain outside the customizable content.
        recordRoot.Children.Add(recordChildren[1]); recordRoot.Children.Add(recordChildren[2]);
        var captureGroup = new StackPanel(); captureGroup.Children.Add(recordChildren[3]); captureGroup.Children.Add(recordChildren[4]);
        var recordBlocks = new StackPanel(); recordBlocks.Children.Add(options); recordBlocks.Children.Add(captureGroup); recordBlocks.Children.Add(recordChildren[5]); recordRoot.Children.Add(recordBlocks);
        Add("Record", "page", "На странице", recordBlocks, "Общие параметры", "Мониторы и управление записью", "Папка сохранения");
        Add("Record", "options", "Общие параметры", options, "Длительность повторов", "Звук в записях");
        var settingColumns = (Grid)PageSettings.Content;
        var behavior = (StackPanel)((StackPanel)((Border)settingColumns.Children[1]).Child).Children[1];
        Add("Settings", "columns", "Колонки", settingColumns, "Оформление", "Настройки приложения");
        Add("Settings", "behavior", "Настройки приложения", behavior, "Запуск с Windows", "Сворачивание в трей", "Уведомления", "Подсказки поверх экрана", "Звук микрофона", "Автозагрузка Audio Center", "Обновления", "Горячие клавиши", "Версия и проверка обновлений");
        var effects = (StackPanel)EffectsScroll.Content;
        // Keep hardware capability messages visible; only the controls can be hidden.
        var effectChildren = effects.Children.Cast<UIElement>().ToArray(); effects.Children.Clear(); effects.Children.Add(effectChildren[0]);
        var effectBlocks = new StackPanel(); effectBlocks.Children.Add(effectChildren[1]); effectBlocks.Children.Add(effectChildren[2]); effects.Children.Add(effectBlocks);
        Add("Effects", "page", "На странице", effectBlocks, "Эффекты устройства", "Проверка каналов");
        if (AutoClickerHost.Content is AutoClickerPanel clicker)
            foreach (var group in clicker.LayoutGroups) { if (!viewGroups.TryGetValue("AutoClicker", out var list)) viewGroups["AutoClicker"] = list = new(); list.Add(group); settings.ViewBlocks!.TryGetValue(group.Id, out var saved); group.Apply(saved); }
        InitCanvasBoards();
        InitNavigationLayout();
        UpdateLayoutButton();
    }
    private void UpdateLayoutButton()
    {
        foreach (var pair in layoutBoards) if (pair.Key != layoutPage && pair.Value.Editing) pair.Value.Finish(false);
        if (CustomizeViewButton == null) return;
        CustomizeViewButton.Visibility = layoutBoards.ContainsKey(layoutPage) ? Visibility.Visible : Visibility.Collapsed;
        CustomizeViewButton.Content = layoutBoards.TryGetValue(layoutPage, out var board) && board.Editing ? "Редактирование вида" : "Изменить вид";
    }
    private void CustomizeView_Click(object sender, RoutedEventArgs e)
    {
        if (layoutBoards.TryGetValue(layoutPage, out var board)) board.Begin();
    }
}
