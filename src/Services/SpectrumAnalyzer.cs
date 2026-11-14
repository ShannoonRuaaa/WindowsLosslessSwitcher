using NAudio.Dsp;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Pure DSP for the mini-spectrograph: interleaved stereo f32 @ 48 kHz in, six normalized bar
/// magnitudes out. Hann-windowed 2048-point FFT every 1024 new mono samples (~47 Hz), max-bin
/// per log-spaced band, dB scaling with a −60 dB floor and a gentle high-band tilt, fast attack
/// with ~300 ms exponential decay. Single-writer (the capture thread); readers take the latest
/// published array by reference swap. No allocations on the sample path except the 24-byte
/// publish per hop.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int BarCount = 6;

    private const int SampleRate = 48000;
    private const int FftSize = 2048;
    private const int FftOrder = 11;
    private const int RingMask = FftSize * 2 - 1;
    private const int HopSize = 1024;
    private const double HopSeconds = HopSize / (double)SampleRate;
    private const double NoiseFloorDb = -60;
    // NAudio's forward FFT scales by 1/N; a full-scale sine under a Hann window then peaks at
    // ≈ 0.25 (A/2 × coherent gain 0.5), so +12.04 dB re-references magnitudes to dBFS.
    private const double FullScaleCalibrationDb = 12.04;

    // Geometric band edges 50 Hz → 16 kHz.
    private static readonly double[] BandEdgesHz = [50, 130, 340, 890, 2330, 6110, 16000];

    private readonly float[] _ring = new float[FftSize * 2];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly int[] _bandStartBin = new int[BarCount];
    private readonly int[] _bandEndBin = new int[BarCount];
    private readonly double[] _bandTiltDb = new double[BarCount];
    private readonly float[] _display = new float[BarCount];
    private readonly double _decayFactor = Math.Exp(-HopSeconds / 0.30);

    private int _writeIndex;
    private int _samplesSinceHop;
    private int _silentSamples = FftSize;
    private volatile float[] _latestBars = new float[BarCount];
    private volatile int _hopsSinceNonSilent = int.MaxValue;

    public SpectrumAnalyzer()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
        }

        var binWidth = SampleRate / (double)FftSize;
        for (var band = 0; band < BarCount; band++)
        {
            _bandStartBin[band] = Math.Max(1, (int)Math.Round(BandEdgesHz[band] / binWidth));
            _bandEndBin[band] = Math.Min(FftSize / 2 - 1, (int)Math.Round(BandEdgesHz[band + 1] / binWidth));
            var centerHz = Math.Sqrt(BandEdgesHz[band] * BandEdgesHz[band + 1]);
            // +2 dB/octave above 1 kHz so typical program material doesn't leave the treble bars dead.
            _bandTiltDb[band] = centerHz > 1000 ? 2 * Math.Log2(centerHz / 1000) : 0;
        }
    }

    /// <summary>Latest published bars (0..1, length <see cref="BarCount"/>). Do not mutate.</summary>
    public float[] LatestBars => _latestBars;

    /// <summary>True when the most recent FFT hop saw only floor-level energy.</summary>
    public bool LastHopWasSilent => _hopsSinceNonSilent > 0;

    /// <summary>Seconds since the analyzer last saw non-silent audio (∞ before the first hop).</summary>
    public double SecondsSinceNonSilent =>
        _hopsSinceNonSilent == int.MaxValue ? double.PositiveInfinity : _hopsSinceNonSilent * HopSeconds;

    /// <summary>Capture-thread entry point: interleaved stereo f32 samples.</summary>
    public void ProcessSamples(float[] interleaved, int count)
    {
        for (var i = 0; i + 1 < count; i += 2)
        {
            _ring[_writeIndex] = 0.5f * (interleaved[i] + interleaved[i + 1]);
            _silentSamples = _ring[_writeIndex] == 0 ? Math.Min(FftSize, _silentSamples + 1) : 0;
            _writeIndex = (_writeIndex + 1) & RingMask;
            if (++_samplesSinceHop >= HopSize)
            {
                _samplesSinceHop = 0;
                RunHop();
            }
        }
    }

    private void RunHop()
    {
        // Latest FftSize mono samples end at _writeIndex.
        var start = (_writeIndex - FftSize) & RingMask;
        var silent = _silentSamples >= FftSize;
        for (var i = 0; !silent && i < FftSize; i++)
        {
            var sample = _ring[(start + i) & RingMask] * _window[i];
            _fft[i].X = sample;
            _fft[i].Y = 0;
        }

        if (!silent) FastFourierTransform.FFT(true, FftOrder, _fft);

        var anyAboveFloor = false;
        var published = new float[BarCount];
        for (var band = 0; band < BarCount; band++)
        {
            double maxMagnitudeSquared = 0;
            for (var bin = _bandStartBin[band]; !silent && bin <= _bandEndBin[band]; bin++)
            {
                var magnitudeSquared = (_fft[bin].X * (double)_fft[bin].X + _fft[bin].Y * (double)_fft[bin].Y);
                if (magnitudeSquared > maxMagnitudeSquared)
                {
                    maxMagnitudeSquared = magnitudeSquared;
                }
            }

            var db = 10 * Math.Log10(maxMagnitudeSquared + 1e-24) + FullScaleCalibrationDb + _bandTiltDb[band];
            var raw = (float)Math.Clamp((db - NoiseFloorDb) / -NoiseFloorDb, 0, 1);
            if (raw > 0)
            {
                anyAboveFloor = true;
            }

            var current = _display[band];
            _display[band] = raw > current
                ? current + 0.55f * (raw - current)
                : raw + (float)((current - raw) * _decayFactor);
            published[band] = _display[band];
        }

        _hopsSinceNonSilent = anyAboveFloor
            ? 0
            : _hopsSinceNonSilent == int.MaxValue ? int.MaxValue : _hopsSinceNonSilent + 1;
        _latestBars = published;
    }

    /// <summary>Clears all state (between capture sessions).</summary>
    public void Reset()
    {
        Array.Clear(_ring);
        Array.Clear(_display);
        _writeIndex = 0;
        _samplesSinceHop = 0;
        _silentSamples = FftSize;
        _hopsSinceNonSilent = int.MaxValue;
        _latestBars = new float[BarCount];
    }
}
