#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using YARG.Audio.BASS.Effects;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    ///     Processes an incoming microphone signal, splitting it into an equalized stream for pitch analysis
    ///     and an effects stream (with reverb and volume control) for live vocal monitoring.
    /// </summary>
    internal sealed class BassMicSignal : IBassMicSampleSource, IDisposable
    {
        private static readonly PeakEQParameters LowAnalysisEq = new()
        {
            fBandwidth = 2.5f,
            fCenter = 20f,
            fGain = -10f,
        };

        private static readonly PeakEQParameters HighAnalysisEq = new()
        {
            fBandwidth = 2.5f,
            fCenter = 10_000f,
            fGain = -10f,
        };

        private static readonly BQFParameters MonitorHighPass1Params = new()
        {
            lFilter = BQFType.HighPass,
            fCenter = 145f,
            fQ = 0.707f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly BQFParameters MonitorHighPass2Params = new()
        {
            lFilter = BQFType.HighPass,
            fCenter = 140f,
            fQ = 0.707f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly BQFParameters MonitorAirCutLowPassParams = new()
        {
            lFilter = BQFType.LowPass,
            fCenter = 15_000f,
            fQ = 0.707f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly PeakEQParameters MonitorBoxinessScoopParams = new()
        {
            fBandwidth = 1.5f,
            fCenter = 400f,
            fGain = -2.0f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly PeakEQParameters MonitorPresenceBiteParams = new()
        {
            fBandwidth = 1.0f,
            fCenter = 3500f,
            fGain = 2.5f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly PeakEQParameters MonitorDeChParams = new()
        {
            fBandwidth = 1.2f,
            fCenter = 5500f,
            fGain = -2.5f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly PeakEQParameters MonitorDeEssParams = new()
        {
            fBandwidth = 1.0f,
            fCenter = 7200f,
            fGain = -2.5f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly PeakEQParameters MonitorAirShelfParams = new()
        {
            fBandwidth = 1.5f,
            fCenter = 10_500f,
            fGain = 2.0f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly DampParameters MonitorLevelerParams = new()
        {
            fTarget = 0.55f,
            fQuiet = 0.025f,
            fRate = 0.02f,
            fGain = 3.0f,
            fDelay = 0.10f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly CompressorParameters MonitorCompressorParams = new()
        {
            fThreshold = -15.0f,
            fRatio = 3.2f,
            fAttack = 8.0f,
            fRelease = 140.0f,
            fGain = 3.0f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly EchoParameters MonitorEchoParams = new()
        {
            fDryMix = 1.0f,
            fWetMix = 0.10f,
            fFeedback = 0.10f,
            fDelay = 0.095f,
            bStereo = 0,
            lChannel = FXChannelFlags.All,
        };

        private readonly EchoParameters _vocalDelay = new()
        {
            fDryMix = 1f, fWetMix = 0.35f, fFeedback = 0.32f, fDelay = 0.30f,
            lChannel = FXChannelFlags.All,
        };

        private readonly DistortionParameters _vocalDrive = new()
        {
            fDrive = 0.6f, fDryMix = 0.65f, fWetMix = 0.15f, fFeedback = 0f, fVolume = 0.55f,
            lChannel = FXChannelFlags.All,
        };

        private static readonly CompressorParameters MonitorLimiterParams = new()
        {
            fThreshold = -1.0f,
            fRatio = 20.0f,
            fAttack = 0.1f,
            fRelease = 30.0f,
            fGain = 0.0f,
            lChannel = FXChannelFlags.All,
        };

        private const float MONITOR_NOISE_GATE_THRESHOLD = 0.032f;
        private const float MONITOR_NOISE_GATE_FLOOR_GAIN = 0.08f;
        private const float MONITOR_NOISE_GATE_ATTACK_MS = 5.0f;
        private const float MONITOR_NOISE_GATE_HOLD_MS = 50.0f;
        private const float MONITOR_NOISE_GATE_RELEASE_MS = 120.0f;

        private const float MONITOR_REVERB_DRY_MIX = 1.0f;
        private const float MONITOR_REVERB_ROOM_SIZE = 0.42f;
        private const float MONITOR_REVERB_DAMP = 0.35f;
        private const float MONITOR_REVERB_WIDTH = 0.0f;

        private int _echoFx;
        private int _driveFx;
        private BassPitchShiftDsp? _pitchShift;
        private int _talkboxHandle;
        private VocalTalkbox? _talkbox;
        private VocalFxPreset _preset;
        private float _standardReverbWet;

        private readonly string          _name;
        private readonly BassNoiseGateDsp _noiseGate;
        private readonly IBassReverbDsp   _reverb;
        private readonly int              _sourceChannels;
        private readonly int              _sourceHandle;
        private readonly int[]?           _channelMap;
        private readonly Dictionary<int, RecordingEffects> _recordingEffects = new();

        private readonly object       _streamLock = new();
        private          int          _analysisHandle;
        private          BassMonitor? _monitor;
        private          int          _monitorHandle;

        private BassMicSignal(string name, int sampleRate, int sourceHandle, int sourceChannels, int[]? channelMap,
            int monitorHandle, int analysisHandle, BassNoiseGateDsp noiseGate, IBassReverbDsp reverb)
        {
            _name = name;
            SampleRate = sampleRate;
            _sourceHandle = sourceHandle;
            _sourceChannels = sourceChannels;
            _channelMap = channelMap;
            _monitorHandle = monitorHandle;
            _analysisHandle = analysisHandle;
            _noiseGate = noiseGate;
            _reverb = reverb;
        }

        public int SampleRate { get; }

        public bool IsValid
        {
            get
            {
                lock (_streamLock)
                {
                    return _analysisHandle != 0;
                }
            }
        }

        public unsafe int Read(Span<float> destination)
        {
            lock (_streamLock)
            {
                if (_analysisHandle == 0)
                {
                    return -1;
                }

                fixed (float* pointer = destination)
                {
                    int bytesRead = Bass.ChannelGetData(_analysisHandle, (IntPtr) pointer,
                        checked(destination.Length * sizeof(float)));
                    return bytesRead < 0 ? -1 : bytesRead / sizeof(float);
                }
            }
        }

        public int GetBacklogBytes()
        {
            lock (_streamLock)
            {
                if (_analysisHandle == 0)
                {
                    return -1;
                }

                int backlogBytes = BassMix.SplitStreamGetAvailable(_analysisHandle);
                return backlogBytes < 0 ? -1 : backlogBytes / _sourceChannels;
            }
        }

        public bool TryCreateRecordingChannel(bool withEffects, out int handle)
        {
            lock (_streamLock)
            {
                if (_sourceHandle == 0)
                {
                    handle = 0;
                    return false;
                }

                handle = BassMix.CreateSplitStream(_sourceHandle,
                    BassFlags.Decode | BassFlags.Float | BassFlags.SplitPosition, _channelMap);
                if (handle == 0)
                {
                    return false;
                }

                if (withEffects && !AddMonitoringEffects(handle, out _))
                {
                    FreeStream(ref handle);
                    return false;
                }

                BassNoiseGateDsp? noiseGate = null;
                IBassReverbDsp? reverb = null;
                if (withEffects)
                {
                    noiseGate = BassNoiseGateDsp.Attach(handle,
                        MONITOR_NOISE_GATE_THRESHOLD, MONITOR_NOISE_GATE_FLOOR_GAIN,
                        MONITOR_NOISE_GATE_ATTACK_MS, MONITOR_NOISE_GATE_HOLD_MS,
                        MONITOR_NOISE_GATE_RELEASE_MS, priority: 5);
                    if (noiseGate == null)
                    {
                        FreeStream(ref handle);
                        return false;
                    }

                    reverb = BassHelpers.CreateReverb(GetReverbMode(), handle, dryMix: MONITOR_REVERB_DRY_MIX,
                        wetMix: GetReverbWet(), roomSize: MONITOR_REVERB_ROOM_SIZE, damp: MONITOR_REVERB_DAMP,
                        width: MONITOR_REVERB_WIDTH, priority: 1);
                    if (reverb == null)
                    {
                        noiseGate.Dispose();
                        FreeStream(ref handle);
                        return false;
                    }
                }

                if (!BassMix.SplitStreamReset(handle, 0))
                {
                    reverb?.Dispose();
                    noiseGate?.Dispose();
                    FreeStream(ref handle);
                    return false;
                }

                _recordingEffects.Add(handle,
                    new RecordingEffects(noiseGate, reverb));
                return true;
            }
        }

        public void ReleaseRecordingChannel(int handle)
        {
            lock (_streamLock)
            {
                if (handle == 0)
                {
                    return;
                }

                if (_recordingEffects.Remove(handle, out RecordingEffects effects))
                {
                    effects.Dispose();
                }

                Bass.StreamFree(handle);
            }
        }

        public bool ResetAnalysis()
        {
            lock (_streamLock)
            {
                if (_analysisHandle != 0 && BassMix.SplitStreamReset(_analysisHandle, 0))
                {
                    return true;
                }

                YargLogger.LogFormatError("Failed to reset mic '{0}' analysis split: {1}", _name, Bass.LastError);
                return false;
            }
        }

        public bool ResetToLive() => ResetAnalysis();

        public void Dispose()
        {
            var monitor = _monitor;
            _monitor = null;
            monitor?.Dispose();

            lock (_streamLock)
            {
                foreach (var recording in _recordingEffects)
                {
                    recording.Value.Dispose();
                    Bass.StreamFree(recording.Key);
                }
                _recordingEffects.Clear();
                _pitchShift?.Dispose();
                _reverb.Dispose();
                _noiseGate.Dispose();
                FreeStream(ref _analysisHandle);
                FreeStream(ref _monitorHandle);
            }
        }

        public static BassMicSignal? Create(int sourceHandle, int[]? channelMap, int sampleRate, string name,
            BassAudioRouter router, double monitoringLevel, bool applyAnalysisEq, Action? attached = null,
            Action? detached = null)
        {
            int monitorHandle = 0;
            int analysisHandle = 0;
            BassNoiseGateDsp? noiseGate = null;
            IBassReverbDsp? reverb = null;

            try
            {
                monitorHandle = BassMix.CreateSplitStream(sourceHandle,
                    BassFlags.Decode | BassFlags.Float | BassFlags.SplitPosition, channelMap);
                if (monitorHandle == 0)
                {
                    YargLogger.LogFormatError("Failed to create mic '{0}' monitor split: {1}", name, Bass.LastError);
                    return null;
                }

                analysisHandle = BassMix.CreateSplitStream(sourceHandle,
                    BassFlags.Decode | BassFlags.Float | BassFlags.SplitPosition | BassFlags.SplitSlave, channelMap);
                if (analysisHandle == 0)
                {
                    YargLogger.LogFormatError("Failed to create mic '{0}' analysis split: {1}", name, Bass.LastError);
                    return null;
                }

                if (applyAnalysisEq && (BassHelpers.AddEqToChannel(analysisHandle, LowAnalysisEq) == 0 ||
                    BassHelpers.AddEqToChannel(analysisHandle, HighAnalysisEq) == 0))
                {
                    YargLogger.LogFormatError("Failed to add EQ to mic '{0}' analysis split: {1}", name,
                        Bass.LastError);
                    return null;
                }

                if (!AddMonitoringEffects(monitorHandle, out int echoFx))
                {
                    YargLogger.LogFormatError("Failed to add dynamics to mic '{0}' monitor split: {1}", name,
                        Bass.LastError);
                    return null;
                }

                noiseGate = BassNoiseGateDsp.Attach(monitorHandle,
                    MONITOR_NOISE_GATE_THRESHOLD, MONITOR_NOISE_GATE_FLOOR_GAIN,
                    MONITOR_NOISE_GATE_ATTACK_MS, MONITOR_NOISE_GATE_HOLD_MS,
                    MONITOR_NOISE_GATE_RELEASE_MS, priority: 5);
                if (noiseGate == null)
                {
                    return null;
                }

                reverb = BassHelpers.CreateReverb(GetReverbMode(), monitorHandle, dryMix: MONITOR_REVERB_DRY_MIX,
                    wetMix: GetReverbWet(), roomSize: MONITOR_REVERB_ROOM_SIZE, damp: MONITOR_REVERB_DAMP,
                    width: MONITOR_REVERB_WIDTH, priority: 1);
                if (reverb == null)
                {
                    YargLogger.LogError($"Failed to add reverb to mic '{name}' monitor split");
                    return null;
                }

                var info = Bass.ChannelGetInfo(sourceHandle);
                int sourceChannels = info.Channels > 0 ? info.Channels : 1;
                var mapCopy = channelMap == null ? null : (int[]) channelMap.Clone();
                var signal = new BassMicSignal(name, sampleRate, sourceHandle, sourceChannels, mapCopy,
                    monitorHandle, analysisHandle, noiseGate, reverb)
                {
                    _echoFx = echoFx,
                    _standardReverbWet = GetReverbWet(),
                };
                monitorHandle = 0;
                analysisHandle = 0;
                noiseGate = null;
                reverb = null;

                var source = new BassMonitorSource(signal._monitorHandle, signal._reverb.RequestReset);
                signal._monitor = router.RegisterMonitor(source, monitoringLevel, attached, detached);
                if (signal._monitor != null)
                {
                    return signal;
                }

                YargLogger.LogError($"Failed to register mic '{name}' monitor with active audio output");
                signal.Dispose();
                return null;
            }
            finally
            {
                noiseGate?.Dispose();
                reverb?.Dispose();
                FreeStream(ref analysisHandle);
                FreeStream(ref monitorHandle);
            }
        }

        public bool ResetMonitor()
        {
            lock (_streamLock)
            {
                if (_monitorHandle == 0 || !BassMix.SplitStreamReset(_monitorHandle, 0))
                {
                    YargLogger.LogFormatError("Failed to reset mic '{0}' monitor split: {1}", _name, Bass.LastError);
                    return false;
                }

                return true;
            }
        }

        public void SetMonitoringLevel(double volume) => _monitor?.SetVolume(volume);

        public void SetReverbLevel(float wet)
        {
            lock (_streamLock)
            {
                _standardReverbWet = wet;
                if (_preset != VocalFxPreset.Reverb)
                {
                    SetMonitorReverb(wet);
                }
                foreach (var pair in _recordingEffects)
                {
                    pair.Value.SetReverbWet(wet);
                }
            }
        }

        private bool SetMonitorReverb(float wet) =>
            _reverb.SetParams(MONITOR_REVERB_DRY_MIX, wet, MONITOR_REVERB_ROOM_SIZE, MONITOR_REVERB_DAMP,
                MONITOR_REVERB_WIDTH);

        public void SetPreset(VocalFxSettings settings)
        {
            lock (_streamLock)
            {
                if (_monitorHandle == 0)
                {
                    return;
                }

                var preset = settings.Preset;
                bool changed = preset != _preset;
                if (changed)
                {
                    if (_driveFx != 0)
                    {
                        if (!Bass.ChannelRemoveFX(_monitorHandle, _driveFx))
                        {
                            YargLogger.LogError($"Could not remove distortion on '{_name}': {Bass.LastError}");
                            return;
                        }
                        _driveFx = 0;
                    }
                    if (_talkboxHandle != 0)
                    {
                        if (!Bass.ChannelRemoveDSP(_monitorHandle, _talkboxHandle))
                        {
                            YargLogger.LogError($"Could not remove talkbox on '{_name}': {Bass.LastError}");
                            return;
                        }
                        _talkboxHandle = 0;
                        _talkbox = null;
                    }
                    _pitchShift?.Dispose();
                    _pitchShift = null;
                    if (_preset == VocalFxPreset.Delay || preset == VocalFxPreset.Delay)
                        Bass.FXReset(_echoFx);
                    if (_preset == VocalFxPreset.Delay && !Bass.FXSetParameters(_echoFx, MonitorEchoParams))
                        YargLogger.LogError($"Could not restore vocal echo on '{_name}'");
                    if (_preset == VocalFxPreset.Reverb || preset == VocalFxPreset.Reverb)
                        _reverb.RequestReset();
                    if (_preset == VocalFxPreset.Reverb)
                        SetMonitorReverb(_standardReverbWet);
                }

                float x = settings.X, y = settings.Y;
                switch (preset)
                {
                    case VocalFxPreset.Reverb:
                        _reverb.SetParams(MONITOR_REVERB_DRY_MIX, 1.5f * y, 0.12f + 0.60f * x,
                            MONITOR_REVERB_DAMP, MONITOR_REVERB_WIDTH);
                        break;
                    case VocalFxPreset.Delay:
                        _vocalDelay.fDelay = 0.05f + 0.50f * x;
                        _vocalDelay.fFeedback = 0.64f * y;
                        Bass.FXSetParameters(_echoFx, _vocalDelay);
                        break;
                    case VocalFxPreset.Distortion:
                        _vocalDrive.fDrive = 0.2f + 0.8f * x;
                        _vocalDrive.fWetMix = 0.30f * y;
                        _vocalDrive.fDryMix = 1f - 0.70f * y;
                        if (_driveFx == 0)
                            _driveFx = BassHelpers.FXAddParameters(_monitorHandle, EffectType.Distortion, _vocalDrive, priority: 3);
                        else
                            Bass.FXSetParameters(_driveFx, _vocalDrive);
                        break;
                    case VocalFxPreset.AutoTune:
                        if (changed)
                            _pitchShift = BassPitchShiftDsp.Attach(_monitorHandle, priority: 3);
                        _pitchShift?.SetRatio(settings.PitchRatio);
                        break;
                    case VocalFxPreset.Talkbox:
                        if (changed && _talkboxHandle == 0)
                        {
                            _talkbox = new VocalTalkbox(SampleRate);
                            _talkboxHandle = Bass.ChannelSetDSP(_monitorHandle, _talkbox.Process, IntPtr.Zero, 3);
                            if (_talkboxHandle == 0)
                                YargLogger.LogError($"Could not attach talkbox on '{_name}': {Bass.LastError}");
                        }
                        _talkbox?.SetControls(x, y);
                        break;
                }
                _preset = preset;
            }
        }

        private static float GetReverbWet() => SettingsManager.Settings?.VocalReverb?.Value ?? 0.25f;

        private static ReverbMode GetReverbMode() =>
            SettingsManager.Settings?.CurrentReverbMode ?? ReverbMode.Performance;

        private static bool AddMonitoringEffects(int handle, out int echoFx)
        {
            echoFx = 0;
            if (BassHelpers.FXAddParameters(handle, EffectType.BQF, MonitorHighPass1Params, priority: 12) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.BQF, MonitorHighPass2Params, priority: 11) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.BQF, MonitorAirCutLowPassParams, priority: 10) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.PeakEQ, MonitorBoxinessScoopParams, priority: 9) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.PeakEQ, MonitorPresenceBiteParams, priority: 8) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.PeakEQ, MonitorDeChParams, priority: 7) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.PeakEQ, MonitorDeEssParams, priority: 6) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.PeakEQ, MonitorAirShelfParams, priority: 5) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.Damp, MonitorLevelerParams, priority: 4) == 0)
            {
                return false;
            }

            if (BassHelpers.FXAddParameters(handle, EffectType.Compressor, MonitorCompressorParams, priority: 3) == 0)
            {
                return false;
            }

            echoFx = BassHelpers.FXAddParameters(handle, EffectType.Echo, MonitorEchoParams, priority: 2);
            if (echoFx == 0)
            {
                return false;
            }

            return BassHelpers.FXAddParameters(handle, EffectType.Compressor, MonitorLimiterParams, priority: 0) != 0;
        }

        private readonly struct RecordingEffects
        {
            private readonly BassNoiseGateDsp? _noiseGate;
            private readonly IBassReverbDsp?    _reverb;

            public RecordingEffects(BassNoiseGateDsp? noiseGate, IBassReverbDsp? reverb)
            {
                _noiseGate = noiseGate;
                _reverb = reverb;
            }

            public void SetReverbWet(float wet) =>
                _reverb?.SetParams(MONITOR_REVERB_DRY_MIX, wet, MONITOR_REVERB_ROOM_SIZE, MONITOR_REVERB_DAMP,
                    MONITOR_REVERB_WIDTH);

            public void Dispose()
            {
                _reverb?.Dispose();
                _noiseGate?.Dispose();
            }
        }

        private static void FreeStream(ref int handle)
        {
            if (handle == 0)
            {
                return;
            }

            if (!Bass.StreamFree(handle))
            {
                YargLogger.LogFormatError("Failed to free microphone stream {0}: {1}", handle, Bass.LastError);
            }

            handle = 0;
        }
    }
}
