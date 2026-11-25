using System;

namespace YARG.Audio.BASS
{
    // Mono channel vocoder: the singer's spectral envelopes articulate a sawtooth carrier.
    internal sealed class VocalTalkbox
    {
        private struct Band
        {
            public float B, A1, A2;
            private float _mic1, _mic2, _synth1, _synth2, _envelope;

            private float Filter(float input, ref float z1, ref float z2)
            {
                float output = B * input + z1;
                z1 = z2 - A1 * output;
                z2 = -B * input - A2 * output;
                return output;
            }

            public float Process(float mic, float carrier, float attack, float release)
            {
                float level = Math.Abs(Filter(mic, ref _mic1, ref _mic2));
                _envelope += (level - _envelope) * (level > _envelope ? attack : release);
                return _envelope * Filter(carrier, ref _synth1, ref _synth2);
            }
        }

        private readonly Band[] _bands = new Band[12];
        private readonly float _attack, _release;
        private readonly int _rate;
        private volatile float _step, _mix;
        private float _phase;

        public VocalTalkbox(int rate)
        {
            _rate = rate;
            _attack = 1f - MathF.Exp(-1f / (rate * 0.005f));
            _release = 1f - MathF.Exp(-1f / (rate * 0.035f));
            float top = Math.Min(6500f, rate * 0.42f);
            for (int i = 0; i < _bands.Length; i++)
            {
                float hz = 150f * MathF.Pow(top / 150f, i / (_bands.Length - 1f));
                float angle = 2f * MathF.PI * hz / rate;
                float alpha = MathF.Sin(angle) / 4f;
                _bands[i] = new Band
                {
                    B = alpha / (1f + alpha),
                    A1 = -2f * MathF.Cos(angle) / (1f + alpha),
                    A2 = (1f - alpha) / (1f + alpha)
                };
            }
            SetControls(0.5f, 0.5f);
        }

        public void SetControls(float pitch, float mix)
        {
            _step = 65.4064f * MathF.Pow(4f, pitch) / _rate;
            _mix = mix;
        }

        public unsafe void Process(int handle, int channel, IntPtr buffer, int bytes, IntPtr user)
        {
            var samples = (float*) buffer;
            float step = _step, mix = _mix;
            for (int n = 0; n < bytes / sizeof(float); n++)
            {
                float dry = samples[n];
                float carrier = 2f * _phase - 1f;
                _phase += step;
                if (_phase >= 1f) _phase -= 1f;
                float wet = 0;
                for (int b = 0; b < _bands.Length; b++)
                    wet += _bands[b].Process(dry, carrier, _attack, _release);
                samples[n] = dry * (1f - mix) + Math.Clamp(2f * wet, -1f, 1f) * mix;
            }
        }
    }
}
