namespace YARG.Audio.BASS
{
    internal readonly struct VocalFxSettings
    {
        public readonly VocalFxPreset Preset;
        public readonly float X, Y, PitchRatio;

        public VocalFxSettings(VocalFxPreset preset, float x = 0.5f, float y = 0.5f, float pitchRatio = 1f)
        {
            Preset = preset;
            X = x;
            Y = y;
            PitchRatio = pitchRatio;
        }

        public bool Matches(VocalFxSettings other) => Preset == other.Preset && X == other.X &&
            Y == other.Y && PitchRatio == other.PitchRatio;
    }

    internal enum VocalFxPreset
    {
        Standard,
        Reverb,
        Delay,
        Distortion,
        AutoTune,
        Talkbox
    }
}
