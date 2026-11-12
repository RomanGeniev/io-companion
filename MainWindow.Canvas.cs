using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Services;

namespace GraphiteCompanion;

public partial class MainWindow
{
    private void InitCanvasBoards()
    {
        ViewLayoutGroup Old(string id) => viewGroups.Values.SelectMany(x => x).First(g => g.Id == id);
        var migration = new Dictionary<string, (bool Visible, int Rank)>();
        bool Visible(string group, int item) => Old(group).Snapshot().First(x => x.Id == item.ToString()).Visible;
        CanvasCard Card(string id, string title, string old, int index, int zone = 1)
        {
            var previous = Old(old).Snapshot(); bool visible = Visible(old, index);
            if (old == "Eq.editor") visible &= Visible("Eq.columns", 0);
            if (old == "Eq.presets") visible &= Visible("Eq.columns", 1);
            if (old == "Mic.processing") visible &= Visible("Mic.page", 1) && Visible("Mic.columns", 0);
            if (old == "Mic.columns") visible &= Visible("Mic.page", 1);
            migration[id] = (visible, previous.FindIndex(x => x.Id == index.ToString()));
            return new(id, title, Old(old).Take(index), zone);
        }
        SnapBoard Board(string page, params CanvasCard[] cards)
        {
            settings.CanvasLayouts ??= new(); settings.CanvasLayouts.TryGetValue(page, out var saved);
            // Migrate visibility from the old cards without changing users' functional settings.
            if (saved == null)
            {
                saved = new CanvasLayout();
                foreach (var card in cards.OrderBy(c => c.Zone).ThenBy(c => migration.TryGetValue(c.Id, out var old) ? old.Rank : Array.IndexOf(cards, c))) saved.Blocks.Add(new() { Id = card.Id, Zone = card.Zone, Visible = migration.TryGetValue(card.Id, out var old) ? old.Visible : card.Visible });
                foreach (var group in cards.SelectMany(c => c.Inside)) saved.Elements[group.Id] = group.Snapshot();
            }
            var board = new SnapBoard(cards, saved, page == "Eq" ? 1.8 : 1, 1); layoutBoards[page] = board;
            board.ModeChanged += UpdateLayoutButton;
            board.Finished += save => { if (save) { settings.CanvasLayouts[page] = board.Value; ScheduleSave(); } if (page == "Eq") DrawCurve(); };
            return board;
        }
        void Mount(ScrollViewer page, SnapBoard board, params UIElement[] notices)
        {
            page.Content = null; page.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var root = new DockPanel(); foreach (var notice in notices) { if (notice is FrameworkElement { Parent: Panel parent }) parent.Children.Remove(notice); DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice); }
            root.Children.Add(board); page.Content = root;
        }
        var modes = Card("Eq.modes", "Режимы", "Eq.editor", 0);
        modes.Inside.Add(new("Eq.modeButtons", "Режимы", (Panel)modes.Element, "Игры", "Музыка", "Фильмы"));
        var eq = Card("Eq.equalizer", "Эквалайзер", "Eq.editor", 1);
        var eqGrid = (Grid)((Border)eq.Element).Child; eqGrid.RowDefinitions[1].MinHeight = 260;
        var eqActions = (WrapPanel)eqGrid.Children[2];
        eq.Inside.Add(new("Eq.actions", "Действия", CanvasParts.Wrap((StackPanel)eqActions.Children[0]), "Сбросить пресет", "Ровно", "Сравнить"));
        eq.Inside.Add(new("Eq.switches", "Отображение", CanvasParts.Wrap((StackPanel)eqActions.Children[1]), "Анализатор звука", "Защита от перегруза"));
        var presets = Card("Eq.saved", "Мои пресеты", "Eq.presets", 0, 2);
        var presetGrid = (Grid)((Border)presets.Element).Child; presetGrid.RowDefinitions[1].MinHeight = 100;
        var presetActions = (StackPanel)presetGrid.Children[presetGrid.Children.Count - 1];
        presets.Inside.Add(new("Eq.presetActions", "Действия", presetActions, "Сохранение и удаление", "Обмен и поиск пресетов"));
        var pads = Card("Eq.pads", "Амбушюры", "Eq.presets", 1, 2);
        pads.Inside.Add(CanvasParts.Tail("Eq.padOptions", (StackPanel)((Border)pads.Element).Child, 1, "Тип устройства", "Материал амбушюр"));
        PageEq.Children.Clear(); PageEq.ColumnDefinitions.Clear(); PageEq.RowDefinitions.Clear(); PageEq.Children.Add(Board("Eq", modes, eq, presets, pads));

        var device = Card("Mic.device", "Устройство и громкость", "Mic.page", 0, 0);
        device.Inside.Add(new("Mic.deviceOptions", "Устройство", (Grid)((Border)device.Element).Child, "Выбор микрофона и уровень", "Громкость и прослушивание"));
        var nr = Card("Mic.noise", "Шумоподавление", "Mic.processing", 0);
        var ptt = Card("Mic.pushToTalk", "Нажми и говори", "Mic.processing", 1);
        var effects = Card("Mic.effects", "Эффекты микрофона", "Mic.processing", 2);
        var voice = Card("Mic.voice", "Коррекция голоса", "Mic.columns", 1, 2);
        // Keep each complete row (label + slider + dB value) together.
        var voiceRows = (Grid)MicVoiceControls.Children[1]; var voiceHost = new StackPanel();
        foreach (var rowIndex in Enumerable.Range(0, voiceRows.RowDefinitions.Count))
        {
            var row = new Grid { MinHeight = voiceRows.RowDefinitions[rowIndex].Height.Value };
            foreach (var col in voiceRows.ColumnDefinitions) row.ColumnDefinitions.Add(new() { Width = col.Width });
            foreach (var child in voiceRows.Children.Cast<FrameworkElement>().Where(c => Grid.GetRow(c) == rowIndex).ToArray()) { voiceRows.Children.Remove(child); Grid.SetRow(child, 0); row.Children.Add(child); }
            row.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsEnabled") { Source = MicVoiceControls });
            voiceHost.Children.Add(row);
        }
        MicVoiceControls.Children.Remove(voiceRows);
        var voiceParent = (StackPanel)MicVoiceControls.Parent; voiceParent.Children.Insert(voiceParent.Children.IndexOf(MicVoiceControls) + 1, voiceHost);
        voice.Inside.Add(new("Mic.voiceValues", "Регуляторы", voiceHost, "Усиление", "Разборчивость", "Высокие частоты", "Срез низких"));
        Mount(PageMic, Board("Mic", device, nr, ptt, voice));

        var common = Card("Record.common", "Общие параметры", "Record.page", 0, 0);
        common.Inside.Add(Old("Record.options"));
        var capture = Card("Record.capture", "Действия с мониторами", "Record.page", 1, 0);
        ((StackPanel)capture.Element).Children.Remove(MonitorPanel);
        var folder = Card("Record.folder", "Папка сохранения", "Record.page", 2, 0);
        Mount(PageRecord, Board("Record", common, capture, folder), RecStatus, RecInfo);

        var appearance = Card("Settings.appearance", "Оформление", "Settings.columns", 0);
        var appearancePanel = (StackPanel)((StackPanel)((Border)appearance.Element).Child).Children[1];
        CanvasParts.Join(appearancePanel, 1, 2);
        appearance.Inside.Add(new("Settings.appearanceOptions", "Оформление", appearancePanel, "Тема", "Цвет интерфейса", "Свой цвет"));
        var behavior = Card("Settings.application", "Настройки приложения", "Settings.columns", 1, 2); behavior.Inside.Add(Old("Settings.behavior"));
        Mount(PageSettings, Board("Settings", appearance, behavior));

        var outputEffects = Card("Effects.device", "Эффекты устройства", "Effects.page", 0, 0);
        var channels = Card("Effects.channels", "Проверка каналов", "Effects.page", 1, 0);
        // Each effect is a card, not one uneditable grid of effects.
        Mount(EffectsScroll, Board("Effects", channels), EffectsEmpty);
        SyncEffectLayout(); SyncMonitorLayout();
        if (AutoClickerHost.Content is AutoClickerPanel clicker)
        {
            var cards = clicker.CreateCanvasCards(); var board = Board("AutoClicker", cards);
            clicker.MountCanvas(board);
            PageAutoClicker.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }
}
