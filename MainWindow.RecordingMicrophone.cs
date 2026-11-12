using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using GraphiteCompanion.Audio;
using NAudio.CoreAudioApi;

namespace GraphiteCompanion;

public partial class MainWindow
{
    private bool updatingRecordingMicrophone;

    private void RefreshRecordingMicrophoneChoices()
    {
        string? defaultId = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultId = device.ID;
        }
        catch { /* No default input is a normal state on a desktop without a microphone. */ }
        UpdateRecordingMicrophoneChoices(activeInputs ?? new List<AudioDeviceInfo>(), defaultId);
    }

    // Updating the list never changes the saved choice or starts capture. A disconnected
    // explicit device stays selected, so another microphone cannot be substituted silently.
    private void UpdateRecordingMicrophoneChoices(IReadOnlyList<AudioDeviceInfo> inputs, string? defaultId)
    {
        if (RecMicrophoneCombo == null) return;
        var currentDefault = inputs.FirstOrDefault(d => AudioDevices.SameId(d.Id, defaultId));
        var choices = new List<MicrophoneChoice> { new(null, null, "Windows · по умолчанию для связи") };
        choices.AddRange(inputs.Select(d => new MicrophoneChoice(d.Id, d.Name, d.Name)));
        string? selectedId = settings.RecordMicrophoneDeviceId;
        var selected = inputs.FirstOrDefault(d => AudioDevices.SameId(d.Id, selectedId));
        if (selectedId != null && selected == null)
        {
            var name = settings.RecordMicrophoneName ?? "Выбранный микрофон";
            choices.Add(new(selectedId, name, name + " · отключён"));
        }
        updatingRecordingMicrophone = true;
        try
        {
            if (RecMicrophoneCombo.ItemsSource is List<MicrophoneChoice> old && old.SequenceEqual(choices)) choices = old;
            else RecMicrophoneCombo.ItemsSource = choices;
            RecMicrophoneCombo.SelectedItem = choices.First(c => AudioDevices.SameId(c.Id, selectedId));
            RecMicrophoneCombo.IsEnabled = true;
        }
        finally { updatingRecordingMicrophone = false; }
        string hint;
        bool missing = settings.RecordMicrophone && (selectedId != null ? selected == null : currentDefault == null);
        if (!settings.RecordMicrophone) hint = "Выберите микрофон, затем включите запись голоса выше.";
        else if (missing) hint = "Микрофон недоступен. Подключите его или выберите другой: запись с голосом не запустится.";
        else hint = selectedId == null ? "Windows сейчас: " + currentDefault!.Name + ". Выбор Windows применяется при запуске буфера или записи." : "Этот микрофон используется в откатах и новых записях.";
        if (AnyRecording) hint += " Текущая запись сохраняет прежний источник.";
        RecMicrophoneHint.Text = hint;
        RecMicrophoneHint.SetResourceReference(TextBlock.ForegroundProperty, missing ? "WarnBrush" : "SubTextBrush");
    }

    private void RecMicrophone_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingRecordingMicrophone || !initialized || RecMicrophoneCombo.SelectedItem is not MicrophoneChoice choice) return;
        if (AudioDevices.SameId(settings.RecordMicrophoneDeviceId, choice.Id)) return;
        settings.RecordMicrophoneDeviceId = choice.Id;
        settings.RecordMicrophoneName = choice.Name;
        RefreshRecordingMicrophoneChoices();
        if (settings.RecordMicrophone) RecSettingsChanged();
        else ScheduleSave(); // Choosing ahead must not enable voice or discard the current replay buffer.
    }
}
