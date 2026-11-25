using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using YARG.Audio.BASS;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Gameplay
{
    // Each inner array is a singer's cues in ascending chart seconds.
    internal sealed class VocalFxTimeline
    {
        private sealed class Cue
        {
            [JsonProperty(Required = Required.Always)]
            public double Time { get; set; }

            [JsonProperty(Required = Required.Always)]
            public VocalFxPreset Preset { get; set; }
        }

        private readonly Cue[][] _singers;

        private VocalFxTimeline(Cue[][] singers) => _singers = singers;

        internal static VocalFxTimeline Load(SongEntry song)
        {
            if (song.SubType == EntryType.Ini)
            {
                var path = Path.Combine(song.ActualLocation, "vocal-fx.json");
                try
                {
                    if (File.Exists(path))
                    {
                        return Parse(File.ReadAllText(path));
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    YargLogger.LogWarning($"Ignoring vocal FX in '{path}': {ex.Message}");
                }
            }

            return new VocalFxTimeline(Array.Empty<Cue[]>());
        }

        internal static VocalFxTimeline Parse(string json)
        {
            var settings = new JsonSerializerSettings { CheckAdditionalContent = true };
            settings.Converters.Add(new StringEnumConverter { AllowIntegerValues = false });
            var singers = JsonConvert.DeserializeObject<Cue[][]>(json, settings);
            if (singers == null)
            {
                throw new JsonSerializationException("Expected an array of singer cue lists.");
            }

            foreach (var cues in singers)
            {
                if (cues == null)
                {
                    throw new JsonSerializationException("Expected a cue list for each singer.");
                }

                double previous = -1;
                foreach (var cue in cues)
                {
                    if (cue == null || double.IsNaN(cue.Time) || double.IsInfinity(cue.Time) ||
                        cue.Time < 0 || cue.Time <= previous || !Enum.IsDefined(typeof(VocalFxPreset), cue.Preset))
                    {
                        throw new JsonSerializationException("Use distinct ascending times in seconds and named presets.");
                    }
                    previous = cue.Time;
                }
            }

            return new VocalFxTimeline(singers);
        }

        internal VocalFxPreset At(int singer, double time)
        {
            if ((uint) singer >= (uint) _singers.Length)
            {
                return VocalFxPreset.Standard;
            }

            var cues = _singers[singer];
            int low = 0, high = cues.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (cues[mid].Time <= time)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low == 0 ? VocalFxPreset.Standard : cues[low - 1].Preset;
        }
    }
}
