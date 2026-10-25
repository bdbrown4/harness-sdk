using Strands.Agents.Bidi;

namespace Strands.Bidi.Audio.Tests;

public sealed class AudioProcessingFeatureTests
{
    [Fact]
    public void DisabledProcessingPreservesEveryPcm16BitAndDetachesTheBuffer()
    {
        var bytes = Enumerable.Range(0, 65536).SelectMany(value => new[] { (byte)value, (byte)(value >> 8) }).ToArray();
        var processor = new ManagedAudioProcessor(16000, 24000, new() { EchoCancellation = false, NoiseSuppression = false, AutomaticGainControl = false });
        var output = processor.Process(bytes);
        Assert.NotSame(bytes, output); Assert.Equal(bytes, output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoiseReductionRetainsSpeechThatStartsImmediately(bool backgroundNoise)
    {
        const int rate = 16000, count = rate * 3;
        var random = new Random(834); var clean = new double[count]; var noisy = new double[count];
        for (var index = 0; index < count; index++)
        {
            var envelope = 0.65 + 0.35 * Math.Sin(2 * Math.PI * 4 * index / rate);
            clean[index] = envelope * (0.08 * Math.Sin(2 * Math.PI * 193 * index / rate) + 0.06 * Math.Sin(2 * Math.PI * 733 * index / rate) + 0.04 * Math.Sin(2 * Math.PI * 1297 * index / rate));
            noisy[index] = clean[index] + (backgroundNoise ? (random.NextDouble() * 2 - 1) * 0.055 : 0);
        }
        var processor = new ManagedAudioProcessor(rate, rate, new() { EchoCancellation = false, AutomaticGainControl = false });
        var output = Process(processor, noisy, 160); var bestSnr = double.NegativeInfinity; var bestDelay = 0;
        for (var delay = 0; delay <= rate / 20; delay++)
        {
            var candidate = Snr(clean, output, rate, rate, delay);
            if (candidate > bestSnr) { bestSnr = candidate; bestDelay = delay; }
        }
        Assert.InRange(CorrelatedGain(clean, output, rate, rate, bestDelay), 0.75, 1.2);
        if (backgroundNoise) Assert.True(bestSnr >= Snr(clean, noisy, rate, rate) + 3, "Speech from the first frame still requires improved SNR; actual " + bestSnr);
        else Assert.True(bestSnr >= 15, "Clean speech must not be learned as background noise; actual SNR " + bestSnr);
    }

    [Fact]
    public void EchoCancellationAttenuatesDelayedEchoAndRetainsIndependentNearSpeech()
    {
        const int rate = 16000, frame = 160, count = rate * 4, delay = rate / 50;
        var random = new Random(812); var far = new double[count]; var clean = new double[count]; var near = new double[count];
        for (var index = 0; index < count; index++)
        {
            far[index] = (random.NextDouble() * 2 - 1) * 0.25;
            clean[index] = index < rate * 2 ? 0 : 0.06 * Math.Sin(2 * Math.PI * 211 * index / rate) + 0.04 * Math.Sin(2 * Math.PI * 617 * index / rate);
            near[index] = clean[index] + (index >= delay ? 0.6 * far[index - delay] : 0) + (index >= delay + 3 ? 0.2 * far[index - delay - 3] : 0);
        }
        var processor = new ManagedAudioProcessor(rate, rate, new() { NoiseSuppression = false, AutomaticGainControl = false, StreamDelayMilliseconds = 20 });
        var output = Process(processor, near, frame, far);
        var echoReduction = RatioDb(Power(near, rate, rate), Power(output, rate, rate));
        Assert.True(echoReduction >= 15, "Echo attenuation after convergence must exceed 15 dB; actual " + echoReduction);
        var speechSnr = Snr(clean, output, rate * 3, rate);
        Assert.True(speechSnr >= 12, "Independent near-end speech must remain intelligible; SNR " + speechSnr);
        var retention = CorrelatedGain(clean, output, rate * 3, rate);
        Assert.InRange(retention, 0.8, 1.2);
    }

    [Fact]
    public void EchoReferenceResamplingSupportsSixteenKhzCaptureAndTwentyFourKhzPlayback()
    {
        const int rate = 16000, outputRate = 24000, frame = 160, count = rate * 4, delay = rate / 50;
        var random = new Random(348); var far = new double[outputRate * 4]; var reference = new double[count];
        for (var index = 0; index < far.Length; index++) far[index] = (index == 0 ? 0 : far[index - 1] * 0.7) + (random.NextDouble() * 2 - 1) * 0.15;
        for (var index = 0; index < count; index++)
        {
            var position = index * 1.5; var whole = (int)position; var fraction = position - whole;
            reference[index] = far[whole] * (1 - fraction) + far[Math.Min(far.Length - 1, whole + 1)] * fraction;
        }
        var clean = new double[count]; var capture = new double[count]; var output = new double[count];
        for (var index = 0; index < count; index++)
        {
            clean[index] = index < rate * 2 ? 0 : 0.06 * Math.Sin(2 * Math.PI * 277 * index / rate);
            capture[index] = clean[index] + (index >= delay ? reference[index - delay] * 0.65 : 0);
        }
        var processor = new ManagedAudioProcessor(rate, outputRate, new() { NoiseSuppression = false, AutomaticGainControl = false, StreamDelayMilliseconds = 20 });
        for (var index = 0; index < count; index += frame)
        {
            processor.AddPlayback(Pcm(far, index * 3 / 2, frame * 3 / 2));
            var bytes = processor.Process(Pcm(capture, index, frame));
            for (var sample = 0; sample < frame; sample++) output[index + sample] = (short)(bytes[sample * 2] | bytes[sample * 2 + 1] << 8) / 32768.0;
        }
        var attenuation = RatioDb(Power(capture, rate, rate), Power(output, rate, rate));
        Assert.True(attenuation >= 15, "Negotiated-rate echo attenuation must exceed 15 dB; actual " + attenuation);
        var speechSnr = Snr(clean, output, rate * 3, rate);
        Assert.True(speechSnr >= 12, "Negotiated-rate independent speech SNR must exceed 12 dB; actual " + speechSnr);
        Assert.InRange(CorrelatedGain(clean, output, rate * 3, rate), 0.8, 1.2);
    }

    [Fact]
    public void NoiseSuppressionImprovesMixedSpeechSnrWithoutMutingTheSpeech()
    {
        const int rate = 16000, count = rate * 3;
        var random = new Random(518); var clean = new double[count]; var noisy = new double[count];
        for (var index = 0; index < count; index++)
        {
            var envelope = index < rate / 2 ? 0 : 0.65 + 0.35 * Math.Sin(2 * Math.PI * 3 * index / rate);
            clean[index] = envelope * (0.08 * Math.Sin(2 * Math.PI * 173 * index / rate) + 0.055 * Math.Sin(2 * Math.PI * 691 * index / rate) + 0.03 * Math.Sin(2 * Math.PI * 1231 * index / rate));
            noisy[index] = clean[index] + (random.NextDouble() * 2 - 1) * 0.055 + 0.025 * Math.Sin(2 * Math.PI * 60 * index / rate);
        }
        var processor = new ManagedAudioProcessor(rate, rate, new() { EchoCancellation = false, AutomaticGainControl = false });
        var output = Process(processor, noisy, 160);
        var inputSnr = Snr(clean, noisy, rate, rate);
        var bestSnr = double.NegativeInfinity; var bestDelay = 0;
        for (var delay = 0; delay <= rate / 20; delay++)
        {
            var candidate = Snr(clean, output, rate, rate, delay);
            if (candidate > bestSnr) { bestSnr = candidate; bestDelay = delay; }
        }
        Assert.True(bestSnr >= inputSnr + 3, "Mixed-speech SNR must improve by 3 dB; input " + inputSnr + ", output " + bestSnr);
        Assert.InRange(CorrelatedGain(clean, output, rate, rate, bestDelay), 0.75, 1.2);
    }

    [Fact]
    public void AutomaticGainNormalizesQuietSpeechWithoutClippingOrAmplifyingSilence()
    {
        const int rate = 16000, count = rate * 3;
        var capture = new double[count];
        for (var index = rate / 2; index < count; index++) capture[index] = (index < rate * 2 ? 0.008 : 0.85) * Math.Sin(2 * Math.PI * 443 * index / rate);
        var processor = new ManagedAudioProcessor(rate, rate, new() { EchoCancellation = false, NoiseSuppression = false });
        var output = Process(processor, capture, 160);
        Assert.InRange(Math.Sqrt(Power(output, rate, rate / 2)), 0.05, 0.2);
        Assert.True(output.All(value => Math.Abs(value) <= 0.999));
        Assert.All(output.Take(rate / 2), value => Assert.Equal(0, value));
    }

    private static double[] Process(ManagedAudioProcessor processor, double[] capture, int frame, double[]? far = null)
    {
        var output = new List<double>();
        for (var index = 0; index < capture.Length; index += frame)
        {
            if (far is not null) processor.AddPlayback(Pcm(far, index, frame));
            var bytes = processor.Process(Pcm(capture, index, frame)); Assert.Equal(frame * 2, bytes.Length);
            for (var sample = 0; sample < frame; sample++) output.Add((short)(bytes[sample * 2] | bytes[sample * 2 + 1] << 8) / 32768.0);
        }
        return output.ToArray();
    }
    private static byte[] Pcm(double[] values, int offset, int count)
    {
        var bytes = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var sample = (short)Math.Round(Math.Max(-0.999, Math.Min(0.999, values[offset + index])) * 32767);
            bytes[index * 2] = (byte)sample; bytes[index * 2 + 1] = (byte)(sample >> 8);
        }
        return bytes;
    }
    private static double Power(double[] values, int start, int count) => values.Skip(start).Take(count).Sum(value => value * value) / count;
    private static double RatioDb(double signal, double error) => 10 * Math.Log10(signal / Math.Max(1e-15, error));
    private static double Snr(double[] expected, double[] actual, int start, int count, int delay = 0)
    {
        var signal = 0.0; var error = 0.0;
        for (var index = start; index < start + count; index++) { signal += expected[index] * expected[index]; var delta = actual[index + delay] - expected[index]; error += delta * delta; }
        return RatioDb(signal, error);
    }
    private static double CorrelatedGain(double[] expected, double[] actual, int start, int count, int delay = 0)
    {
        var cross = 0.0; var power = 0.0;
        for (var index = start; index < start + count; index++) { cross += expected[index] * actual[index + delay]; power += expected[index] * expected[index]; }
        return cross / power;
    }
}
