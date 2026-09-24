using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SDRSharp.NRSC5;

const double outputRate = 744187.5;
double[] rates = [744187.5, 768000, 912000, 1024000, 1200000, 2400000, 4800000];
var random = new Random(334);
var input = new float[65536];
for (var i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 2 - 1);

// SDK DLLs are reference assemblies: inspect their layout without executing them.
var sdkPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../vendor/SDRSharpSDK/lib/SDRSharp.Radio.dll"));
using (var sdk = File.OpenRead(sdkPath))
using (var pe = new PEReader(sdk))
{
    var metadata = pe.GetMetadataReader();
    var type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
        .Single(t => metadata.GetString(t.Name) == "Complex");
    var fields = type.GetFields().Select(metadata.GetFieldDefinition).ToArray();
    Require((type.Attributes & System.Reflection.TypeAttributes.LayoutMask) == System.Reflection.TypeAttributes.SequentialLayout,
        "SDK complex sequential layout");
    Require(fields.Length == 2 && metadata.GetString(fields[0].Name) == "Real" && metadata.GetString(fields[1].Name) == "Imag"
        && fields.All(f => Convert.ToHexString(metadata.GetBlobBytes(f.Signature)) == "060C"), "SDK interleaved float IQ layout");
}

double maxFilterError = 0;
foreach (var rate in rates)
{
    var actual = new PolyphaseResampler();
    var reference = new ReferenceResampler();
    actual.Configure(rate, outputRate);
    reference.Configure(rate, outputRate);
    float[] a = [], b = [];
    // Includes tiny buffers, vector tails, filter-history boundaries and reset/rate changes.
    for (var block = 0; block < 90; block++)
    {
        if (block == 30) { actual.Reset(); reference.Reset(); }
        var configuredRate = block < 60 ? rate : 912000;
        actual.Configure(configuredRate, outputRate);
        reference.Configure(configuredRate, outputRate);
        var count = (block % 6) switch { 0 => 1, 1 => 17, 2 => 63, _ => random.Next(100, 32769) };
        var na = actual.Process(input, count, ref a);
        var nb = reference.Process(input, count, ref b);
        Require(na == nb, $"Output count at {rate}");
        for (var i = 0; i < na * 2; i++)
        {
            var error = Math.Abs(a[i] - b[i]);
            maxFilterError = Math.Max(maxFilterError, error);
            Require(error < 2e-6, $"Filter error at {rate}: {error}");
        }
    }
}
Console.WriteLine($"[OK] Resampler vs Dev 3.3.4; max absolute error {maxFilterError:E3}; Vector256={Vector256.IsHardwareAccelerated}.");

double maxPreviousError = 0;
foreach (var rate in rates)
{
    var actual = new PolyphaseResampler();
    var previous = new Resampler335();
    actual.Configure(rate, outputRate);
    previous.Configure(rate, outputRate);
    float[] a = [], b = [];
    for (var block = 0; block < 40; block++)
    {
        var count = (block % 5) switch { 0 => 1, 1 => 33, _ => random.Next(100, 32769) };
        var na = actual.Process(input, count, ref a);
        var nb = previous.Process(input, count, ref b);
        Require(na == nb, $"Output count vs 3.3.5 at {rate}");
        for (var i = 0; i < na * 2; i++)
        {
            var error = Math.Abs(a[i] - b[i]);
            maxPreviousError = Math.Max(maxPreviousError, error);
            Require(error < 2e-6, $"Filter error vs 3.3.5 at {rate}: {error}");
        }
    }
}
Console.WriteLine($"[OK] Resampler vs Dev 3.3.5; max absolute error {maxPreviousError:E3}; FMA={System.Runtime.Intrinsics.X86.Fma.IsSupported}.");

// Check sample continuity independently of the original implementation's block handling.
foreach (var rate in rates)
{
    var whole = Resample(input, rate, input.Length / 2);
    var split = Resample(input, rate, 137);
    Require(whole.Count == split.Count, "Partition output count");
    // The existing 512-phase bank can round to a neighboring phase when a block
    // subtraction lands on a phase boundary. Allow one phase's interpolation error.
    for (var i = 0; i < whole.Count; i++) Require(Math.Abs(whole[i] - split[i]) < 0.01, $"Partition continuity at {rate}, sample {i}");
}
// Physical filter checks: digital sidebands pass, out-of-band tones are suppressed.
foreach (var rate in new double[] { 768000, 912000, 1200000, 2400000 })
{
    var pass = ToneGain(rate, 200000);
    var stop = ToneGain(rate, Math.Min(450000, rate * 0.49));
    Require(pass > 0.98 && pass < 1.02, $"Sideband gain at {rate}: {pass}");
    Require(stop < 0.02, $"Stopband at {rate}: {stop}");
}
Console.WriteLine("[OK] Block continuity, sideband gain and anti-alias rejection.");

var mixer = new IqMixer();
var referenceMixer = new ReferenceMixer();
var mixed = new float[input.Length];
var mixedReference = new float[input.Length];
double maxMixerError = 0;
for (var block = 0; block < 400; block++)
{
    var offset = block < 100 ? 157321.125 : block < 200 ? 0 : -210123.75;
    var rate = block < 250 ? 912000 : 2400000;
    if (block == 300) { mixer.Reset(); referenceMixer.Reset(); }
    var count = block % 2 == 0 ? input.Length : 274;
    mixer.Process(input.AsSpan(0, count), mixed, offset, rate);
    referenceMixer.Process(input.AsSpan(0, count), mixedReference, offset, rate);
    for (var i = 0; i < count; i++)
    {
        var error = Math.Abs(mixed[i] - mixedReference[i]);
        maxMixerError = Math.Max(maxMixerError, error);
        Require(error < 2e-6, $"Mixer error {error}");
    }
}
mixer.Reset();
mixer.Process(input, mixed, 0, 912000);
Require(input.AsSpan().SequenceEqual(mixed), "Unity oscillator bypass");
Console.WriteLine($"[OK] Mixer phase across blocks, recenter, rate changes and reset; max error {maxMixerError:E3}.");

var ring = new PcmRingBuffer(2048);
var expected = new Queue<short>();
for (var block = 0; block < 1000; block++)
{
    if (block == 500) { ring.EnsureCapacityFrames(4096); expected.Clear(); }
    if (block % 113 == 0) { ring.Clear(); expected.Clear(); }
    var samples = new short[random.Next(0, 12000)];
    for (var i = 0; i < samples.Length; i++) samples[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
    ring.Write(samples);
    for (var i = 0; i < (samples.Length & ~1); i++) expected.Enqueue(samples[i]);
    while (expected.Count > ring.CapacityFrames * 2) expected.Dequeue();
    for (var i = 0; i < random.Next(0, 5000); i++)
    {
        var ok = ring.TryReadFrame(out var left, out var right);
        Require(ok == (expected.Count >= 2), "PCM empty state");
        if (ok) Require(left == expected.Dequeue() / 32768f && right == expected.Dequeue() / 32768f, "PCM order/channels");
    }
    Require(ring.AvailableFrames == expected.Count / 2, "PCM occupancy");
    // The metadata delay rests on this: written minus consumed is exactly what is waiting.
    Require(ring.TotalWrittenFrames - ring.TotalConsumedFrames == ring.AvailableFrames, "PCM position counters");
}
Console.WriteLine("[OK] PCM conversion, wraparound, overflow, stereo alignment, clear and resize.");

var allocationFilter = new PolyphaseResampler();
allocationFilter.Configure(912000, outputRate);
float[] allocationOutput = [];
var pcmInput = new short[4096];
for (var i = 0; i < 20; i++) allocationFilter.Process(input, input.Length / 2, ref allocationOutput);
var iqQueue = new IqBlockQueue { MaxQueuedFloats = 1 << 22 };
for (var i = 0; i < 4; i++)
{
    iqQueue.TryEnqueue(input, 912000, 0, 0);
    iqQueue.TryDequeue(0, out var warm);
    iqQueue.Return(warm.Buffer);
}
var before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 50; i++)
{
    // The full per-block path of 4.0.0: queue on SDR#'s side, mix and resample on the decoder's.
    iqQueue.TryEnqueue(input, 912000, 100000, 0);
    iqQueue.TryDequeue(0, out var block);
    mixer.Process(block.Buffer.AsSpan(0, block.Floats), mixed, 100000, 912000);
    iqQueue.Return(block.Buffer);
    allocationFilter.Process(mixed, input.Length / 2, ref allocationOutput);
    ring.Write(pcmInput);
}
var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Require(allocated == 0, $"Steady-state allocations: {allocated}");
Console.WriteLine("[OK] Zero steady-state allocations in the IQ queue, mixer, resampler and PCM writes.");

if (args.Contains("--benchmark"))
{
    Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}");
    // Three generations side by side in one process, so the comparison does not depend on
    // how busy the machine was when an older number was written down. The load column is
    // the share of one core needed to keep up with the stream in real time, which is what
    // actually matters: a block at 4.8 MS/s covers five times less signal than at 912 kS/s.
    foreach (var rate in rates)
    {
        var current = new PolyphaseResampler();
        var previous = new Resampler335();
        var original = new ReferenceResampler();
        current.Configure(rate, outputRate);
        previous.Configure(rate, outputRate);
        original.Configure(rate, outputRate);
        float[] currentOutput = [], previousOutput = [], originalOutput = [];
        var now = Measure(() => current.Process(input, input.Length / 2, ref currentOutput));
        var dev335 = Measure(() => previous.Process(input, input.Length / 2, ref previousOutput));
        var dev334 = Measure(() => original.Process(input, input.Length / 2, ref originalOutput));
        var signalMs = input.Length / 2 / rate * 1000;
        Console.WriteLine($"Resampler {rate / 1000,6:F1} kS/s: 3.3.4 {dev334:F3}  3.3.5 {dev335:F3}  4.0.0 {now:F3} ms/block " +
                          $"| {dev335 / now:F2}x vs 3.3.5 | load {dev335 / signalMs * 100:F2}% -> {now / signalMs * 100:F2}% of a core");
    }
    foreach (var offset in new double[] { 0, 157321.125 })
    {
        mixer.Reset(); referenceMixer.Reset();
        var fast = Measure(() => mixer.Process(input, mixed, offset, 912000));
        var slow = Measure(() => referenceMixer.Process(input, mixedReference, offset, 912000));
        Console.WriteLine($"Mixer {offset:F1} Hz: original {slow:F3} ms/block, optimized {fast:F3} ms/block, {slow / fast:F2}x");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static List<float> Resample(float[] input, double rate, int blockSize)
{
    var filter = new PolyphaseResampler();
    filter.Configure(rate, outputRate);
    var result = new List<float>();
    float[] output = [];
    for (var start = 0; start < input.Length / 2; start += blockSize)
    {
        var count = Math.Min(blockSize, input.Length / 2 - start);
        var block = input.AsSpan(start * 2, count * 2).ToArray();
        var produced = filter.Process(block, count, ref output);
        result.AddRange(output.AsSpan(0, produced * 2).ToArray());
    }
    return result;
}

static double ToneGain(double rate, double frequency)
{
    var tone = new float[65536];
    for (var i = 0; i < tone.Length / 2; i++)
    {
        tone[i * 2] = (float)Math.Cos(2 * Math.PI * frequency * i / rate);
        tone[i * 2 + 1] = (float)Math.Sin(2 * Math.PI * frequency * i / rate);
    }
    var output = Resample(tone, rate, 4096);
    double power = 0;
    for (var i = 256; i < output.Count; i++) power += output[i] * output[i];
    return Math.Sqrt(power / ((output.Count - 256) / 2));
}

static double Measure(Action action)
{
    // Warm tiered JIT before timing; report median of five batches, no CI speed threshold.
    for (var i = 0; i < 150; i++) action();
    var times = new double[5];
    for (var batch = 0; batch < times.Length; batch++)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 30; i++) action();
        times[batch] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 30;
    }
    Array.Sort(times);
    return times[times.Length / 2];
}

// Original Dev 3.3.4 oscillator, retained only for regression/benchmark comparison.
internal sealed class ReferenceMixer
{
    private double _phase;
    public void Reset() => _phase = 0;
    public void Process(ReadOnlySpan<float> input, Span<float> output, double offset, double rate)
    {
        var step = -2 * Math.PI * offset / rate;
        for (var index = 0; index < input.Length; index += 2)
        {
            var cos = (float)Math.Cos(_phase);
            var sin = (float)Math.Sin(_phase);
            output[index] = input[index] * cos - input[index + 1] * sin;
            output[index + 1] = input[index] * sin + input[index + 1] * cos;
            _phase += step;
            if (_phase > Math.PI) _phase -= 2 * Math.PI;
            else if (_phase < -Math.PI) _phase += 2 * Math.PI;
        }
    }
}
