using System;
using System.Collections.Generic;

namespace GraphiteCompanion.Audio
{
    /// <summary>A 10-band equalizer backend: the headset's C-Media driver or Equalizer APO.</summary>
    public interface IEqualizer : IDisposable
    {
        /// <summary>Short name shown in the UI ("C-Media", "Equalizer APO").</summary>
        string Engine { get; }
        int[] ReadBands();
        bool ReadEnabled();
        void SetEnabled(bool enabled);
        /// <summary>Writes bands; when <paramref name="previous"/> is given only changes need to be sent.</summary>
        void SetBands(IReadOnlyList<int> bands, IReadOnlyList<int>? previous);
    }
}
