#nullable enable
using System;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    /// <summary>
    ///     Unified player microphone device for both ASIO and Shared Audio backends.
    ///     Manages timed input frames for vocal gameplay scoring and coordinates live monitoring.
    /// </summary>
    public sealed class BassMicDevice : MicDevice
    {
        private readonly IBassMicSource  _source;
        private          BassMicAnalyzer _analyzer;
        private readonly object          _lifecycleLock = new();
        private          bool                 _disposed;

        internal BassMicDevice(IBassMicSource source) : base(source.DisplayName)
        {
            _source = source;
            _analyzer = CreateAnalyzer();
            _source.InputChanged += RecreateAnalyzer;
        }

        internal static BassMicDevice? Create(IBassMicSource source)
        {
            try
            {
                return new BassMicDevice(source);
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, $"Failed to initialize microphone '{source.DisplayName}'");
                source.Dispose();
                return null;
            }
        }

        public bool TryCreateRecordingChannel(bool withEffects, out int handle, out int sampleRate)
            => _source.TryCreateRecordingChannel(withEffects, out handle, out sampleRate);

        public void ReleaseRecordingChannel(int handle) => _source.ReleaseRecordingChannel(handle);

        private BassMicAnalyzer CreateAnalyzer() =>
            new(_source, () => IsRecordingOutput, () => InputManager.CurrentInputTime);

        private void RecreateAnalyzer()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _analyzer.Dispose();
                try
                {
                    _analyzer = CreateAnalyzer();
                }
                catch (Exception exception)
                {
                    YargLogger.LogException(exception, $"Failed to recreate analyzer for '{DisplayName}'");
                    return;
                }
            }
        }

        public override int Reset()
        {
            lock (_lifecycleLock)
            {
                bool sourceReset = _source.Reset();
                bool analyzerReset = _analyzer.Reset();
                return sourceReset && analyzerReset ? 0 : -1;
            }
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame)
        {
            lock (_lifecycleLock)
            {
                bool available = _analyzer.DequeueOutputFrame(out frame);
                if (available && !frame.IsHit && frame.Pitch > 0)
                {
                    _pitchMidi = frame.PitchAsMidiNote;
                    _pitchTime = frame.Time;
                }
                return available;
            }
        }

        public override void ClearOutputQueue()
        {
            lock (_lifecycleLock)
            {
                _analyzer.ClearOutputQueue();
            }
        }

        public override void SetMonitoringLevel(float volume) => _source.SetMonitoringLevel(volume);

        public override void SetReverbLevel(float wet) => _source.SetReverbLevel(wet);

        private double _pitchTime = double.NegativeInfinity;
        private float _pitchMidi;
        private VocalFxSettings _vocalFx = new(VocalFxPreset.Standard);

        internal void SetVocalFx(VocalFxPreset preset, float x = 0.5f, float y = 0.5f, float delta = 0f)
        {
            float ratio = 1f;
            if (preset == VocalFxPreset.AutoTune && InputManager.CurrentInputTime - _pitchTime < 0.15)
            {
                float target = MathF.Pow(2f, (MathF.Round(_pitchMidi) - _pitchMidi) * x / 12f);
                float response = 0.16f - 0.155f * y;
                float previous = _vocalFx.Preset == preset ? _vocalFx.PitchRatio : 1f;
                ratio = previous + (target - previous) * (1f - MathF.Exp(-delta / response));
            }

            var settings = new VocalFxSettings(preset, x, y, ratio);
            if (!_vocalFx.Matches(settings))
            {
                _source.SetVocalFx(settings);
                _vocalFx = settings;
            }
        }

        public override MicId Id => new(_source.BaseName, _source.Channel);

        public override SerializedMic Serialize() => new(Id);

        public override MicBufferInfo? GetBufferInfo() => _source.GetBufferInfo();

        protected override void DisposeUnmanagedResources()
        {
            BassMicAnalyzer analyzer;
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.InputChanged -= RecreateAnalyzer;
                analyzer = _analyzer;
            }

            analyzer.Dispose();
            _source.Dispose();
        }
    }
}
