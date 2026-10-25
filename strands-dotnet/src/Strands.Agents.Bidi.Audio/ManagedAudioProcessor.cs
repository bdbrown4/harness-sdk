using NWaves.Filters.Adaptive;
using NWaves.Filters.Base;

namespace Strands.Agents.Bidi;

internal sealed class ManagedAudioProcessor
{
    private readonly object _gate = new();
    private readonly int _inputRate, _outputRate, _delaySamples;
    private readonly BidiAudioProcessingOptions _options;
    private readonly Queue<float> _played = [];
    private readonly Queue<float> _delay = [];
    private readonly List<float> _resample = [];
    private readonly NlmsFilter? _echo;
    private readonly NoiseReducer? _noise;
    private double _resamplePosition;
    private double _gain = 1;

    internal ManagedAudioProcessor(int inputRate, int outputRate, BidiAudioProcessingOptions options)
    {
        if (inputRate is < 8000 or > 192000 || outputRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.StreamDelayMilliseconds is < 0 or > 1000 || !options.EchoCancellation && options.StreamDelayMilliseconds != 0)
            throw new ArgumentException("The 0-1000 ms delay hint requires echo cancellation.", nameof(options));
        _inputRate = inputRate; _outputRate = outputRate; _options = options;
        _delaySamples = checked(inputRate * options.StreamDelayMilliseconds / 1000);
        // Conservative adaptation preserves independent near speech during double-talk.
        if (options.EchoCancellation) _echo = new(Math.Max(128, inputRate / 100), mu: 0.04f, eps: 0.000001f);
        if (options.NoiseSuppression) _noise = new(inputRate);
        ClearPlayback();
    }

    internal byte[] Process(byte[] capture)
    {
        if (capture.Length % 2 != 0) throw new ArgumentException("PCM16 capture requires complete samples.", nameof(capture));
        if (capture.Length == 0) return [];
        if (_echo is null && _noise is null && !_options.AutomaticGainControl) return capture.ToArray();
        lock (_gate)
        {
            var samples = new float[capture.Length / 2];
            for (var index = 0; index < samples.Length; index++)
            {
                var sample = Read(capture, index);
                if (_echo is not null)
                {
                    var reference = _played.Count == 0 ? 0 : _played.Dequeue();
                    if (_delaySamples > 0) { _delay.Enqueue(reference); reference = _delay.Dequeue(); }
                    sample -= _echo.Process(reference, sample);
                }
                samples[index] = _noise?.Process(sample) ?? sample;
            }
            if (_options.AutomaticGainControl) ApplyGain(samples);
            var result = new byte[capture.Length];
            for (var index = 0; index < samples.Length; index++)
            {
                var value = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(samples[index] * 32768.0)));
                result[index * 2] = (byte)value; result[index * 2 + 1] = (byte)(value >> 8);
            }
            return result;
        }
    }

    internal void AddPlayback(byte[] playback)
    {
        if (_echo is null) return;
        if (playback.Length % 2 != 0) throw new ArgumentException("PCM16 playback requires complete samples.", nameof(playback));
        lock (_gate)
        {
            if (_inputRate == _outputRate)
                for (var index = 0; index < playback.Length / 2; index++) EnqueueReference(Read(playback, index));
            else
            {
                for (var index = 0; index < playback.Length / 2; index++) _resample.Add(Read(playback, index));
                var step = (double)_outputRate / _inputRate;
                while (_resamplePosition + 1 < _resample.Count)
                {
                    var position = (int)_resamplePosition; var fraction = (float)(_resamplePosition - position);
                    EnqueueReference(_resample[position] + fraction * (_resample[position + 1] - _resample[position]));
                    _resamplePosition += step;
                }
                var consumed = Math.Min((int)_resamplePosition, Math.Max(0, _resample.Count - 1));
                _resample.RemoveRange(0, consumed); _resamplePosition -= consumed;
            }
        }
    }

    internal void ClearPlayback()
    {
        lock (_gate)
        {
            _played.Clear(); _resample.Clear(); _resamplePosition = 0; _delay.Clear();
            for (var index = 0; index < _delaySamples; index++) _delay.Enqueue(0);
            _echo?.Reset();
        }
    }

    private void EnqueueReference(float sample)
    {
        if (_played.Count >= _inputRate * 2) _played.Dequeue();
        _played.Enqueue(sample);
    }
    private static float Read(byte[] bytes, int index) => (short)(bytes[index * 2] | bytes[index * 2 + 1] << 8) / 32768f;
    private void ApplyGain(float[] samples)
    {
        var power = 0.0; var peak = 0.0;
        foreach (var sample in samples) { power += sample * sample; peak = Math.Max(peak, Math.Abs(sample)); }
        var rms = Math.Sqrt(power / samples.Length);
        if (rms < 0.00001) return;
        var desired = Math.Min(16, Math.Min(0.12 / rms, 0.98 / peak));
        _gain = desired < _gain ? desired : 0.95 * _gain + 0.05 * desired;
        for (var index = 0; index < samples.Length; index++) samples[index] *= (float)_gain;
    }

    private sealed class NoiseReducer : OverlapAddFilter
    {
        private readonly double[] _noise;
        private readonly double[] _power;
        private readonly double[] _gains;
        private readonly double[] _neighbors = new double[17];
        private int _frames;
        internal NoiseReducer(int rate) : base(FftSize(rate) / 4, FftSize(rate))
        {
            _noise = new double[_fftSize / 2 + 1]; _power = new double[_fftSize / 2 + 1];
            _gains = Enumerable.Repeat(1.0, _fftSize / 2 + 1).ToArray();
        }
        protected override void ProcessSpectrum(float[] re, float[] im, float[] filteredRe, float[] filteredIm)
        {
            _frames++;
            for (var index = 0; index < _power.Length; index++) _power[index] = re[index] * re[index] + im[index] * im[index];
            for (var index = 0; index < _power.Length; index++)
            {
                // Estimate the local spectral floor, rather than learning initial speech as noise.
                // The lower quartile rejects narrow speech harmonics; its scale compensates
                // the exponential power distribution of stationary broadband noise.
                var count = 0;
                for (var neighbor = Math.Max(0, index - 8); neighbor <= Math.Min(_power.Length - 1, index + 8); neighbor++) _neighbors[count++] = _power[neighbor];
                Array.Sort(_neighbors, 0, count);
                var estimate = 3.5 * _neighbors[count / 4]; var power = _power[index];
                _noise[index] = _frames == 1 ? estimate : 0.95 * _noise[index] + 0.05 * estimate;
                var desired = Math.Max(0.12, 1 - _noise[index] / Math.Max(1e-12, power));
                _gains[index] = 0.6 * _gains[index] + 0.4 * desired;
            }
            for (var index = 0; index < _power.Length; index++)
            {
                var gain = 0.75 * _gains[index] + 0.125 * _gains[Math.Max(0, index - 1)] + 0.125 * _gains[Math.Min(_gains.Length - 1, index + 1)];
                filteredRe[index] = (float)(re[index] * gain); filteredIm[index] = (float)(im[index] * gain);
            }
        }
        private static int FftSize(int rate) { var size = 256; while (size < rate / 40) size *= 2; return size; }
    }
}
