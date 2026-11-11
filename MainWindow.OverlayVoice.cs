using System;
using System.Linq;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    public sealed record OverlayVoiceState(bool Available, int Selected);

    public partial class MainWindow
    {
        public OverlayVoiceState GetOverlayVoice()
        {
            var store = micSession?.Effects;
            var definition = Effects.Microphone.First(d => d.Title == "Magic Voice");
            int? enabled = store?.Read(definition.Format, definition.EnablePid);
            if (!enabled.HasValue) return new(false, -1);
            int selected = enabled == 0 ? -1 : store!.Read(definition.Format, 3) ?? 3;
            return new(true, Math.Clamp(selected, -1, 3));
        }

        public void SetOverlayVoice(int selected)
        {
            if (selected < -1 || selected > 3) return;
            FlushEffects();
            var store = micSession?.Effects;
            var definition = Effects.Microphone.First(d => d.Title == "Magic Voice");
            if (store?.Read(definition.Format, definition.EnablePid) == null) return;
            try
            {
                if (selected >= 0) store.Write(definition.Format, 3, selected);
                store.Write(definition.Format, definition.EnablePid, selected < 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NotifyChange("Не удалось изменить голос", "\uE7BA", "Проверьте подключение микрофона");
            }
            ReloadEffects();
        }
    }
}
