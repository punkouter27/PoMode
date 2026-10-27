using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using PoMode.API.Audio;
using PoMode.API.Infrastructure;

namespace PoMode.API.Features.SoundSearch;

/// <summary>
/// CLAP (LAION's music-and-speech checkpoint, Apache-2.0): a song and a sentence go into the same
/// 512-dimensional space, so "dark acoustic" can be compared with a recording. The audio tower runs
/// once per song, in the background (<see cref="SoundIndexer"/>); the text tower runs per search.
/// </summary>
/// <remarks>
/// The front end is Hugging Face's <c>ClapFeatureExtractor</c> as this checkpoint configures it, step
/// for step: 48 kHz mono, 10 s chunks (a shorter clip is repeated to fill one, then zero-padded), a
/// 1024-point STFT every 480 samples with <c>center=True</c>, power spectrum, a 64-band Slaney mel
/// bank from 50 Hz to 14 kHz, and <c>10·log10(max(x, 1e-10))</c>. Where the reference crops a longer
/// song at random, this takes up to <see cref="MaxChunks"/> evenly spaced crops and averages their
/// unit vectors, so a song has one stable embedding rather than a lucky ten seconds.
/// </remarks>
public sealed class ClapEncoder(ModelRegistry registry, ILogger<ClapEncoder> logger) : IDisposable
{
    public const int SampleRate = 48000;
    internal const int ChunkSamples = 480000;
    internal const int Frames = 1 + (ChunkSamples / Hop);
    internal const int Mels = 64;
    private const int NFft = 1024;
    private const int Hop = 480;
    private const int MaxChunks = 6;

    /// <summary>CLAP was trained on captions up to 77 tokens; a search is a few words.</summary>
    private const int MaxTokens = 77;

    /// <summary>RoBERTa's sentence markers, ids 0 and 2 in its vocabulary.</summary>
    private const int Bos = 0, Eos = 2;

    /// <summary>The text tower is 127 MB in memory: it stays loaded while someone is searching and is
    /// let go after this long without one, so it is not resident beside HTDemucs all day.</summary>
    private static readonly TimeSpan TextIdle = TimeSpan.FromMinutes(3);

    private static readonly Lazy<float[][]> MelBank = new(() =>
        MelSpectrogram.SlaneyFilterbank(SampleRate, NFft, Mels, 50, 14000));

    private readonly SemaphoreSlim _textGate = new(1, 1);
    private readonly ConcurrentDictionary<string, float[]> _textCache = new(StringComparer.OrdinalIgnoreCase);
    private InferenceSession? _textSession;
    private Tokenizer? _tokenizer;
    private Timer? _textReaper;

    /// <summary>Local models only, as every server-side model: Azure's host does not run them.</summary>
    public bool IsAvailable =>
        !EnvironmentDetector.IsAzureHosted()
        && registry.IsDownloaded(ModelCatalog.ClapAudio) && registry.IsDownloaded(ModelCatalog.ClapText)
        && registry.IsDownloaded(ModelCatalog.ClapVocab) && registry.IsDownloaded(ModelCatalog.ClapMerges);

    /// <summary>One unit vector for a whole recording.</summary>
    public async Task<float[]> EmbedAudioAsync(AudioBuffer audio, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.ClapAudio, ct);
        return await Task.Run(() =>
        {
            var mono = AudioDecoder.ResampleBandLimited(AudioDecoder.ToMono(audio), SampleRate).Samples;
            var chunks = Chunks(mono);
            var input = new float[chunks.Count * Frames * Mels];
            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Features(chunks[i]).CopyTo(input, i * Frames * Mels);
            }

            using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
            using var session = new InferenceSession(modelPath, options);
            using var results = session.Run(
                [NamedOnnxValue.CreateFromTensor("input_features", new DenseTensor<float>(input, [chunks.Count, 1, Frames, Mels]))]);
            var embeds = results[0].AsTensor<float>().ToArray();

            var mean = new float[embeds.Length / chunks.Count];
            for (var i = 0; i < chunks.Count; i++)
            {
                var one = Normalise(embeds.AsSpan(i * mean.Length, mean.Length).ToArray());
                for (var d = 0; d < mean.Length; d++)
                {
                    mean[d] += one[d];
                }
            }
            logger.LogInformation("CLAP: embedded {Seconds:0} s of audio from {Chunks} crops.", audio.DurationSeconds, chunks.Count);
            return Normalise(mean);
        }, ct);
    }

    /// <summary>One unit vector for a phrase; repeated phrases (each keystroke pause) are cached.</summary>
    public async Task<float[]> EmbedTextAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (_textCache.TryGetValue(text, out var cached))
        {
            return cached;
        }
        var modelPath = await registry.EnsureAsync(ModelCatalog.ClapText, ct);
        var vocabPath = await registry.EnsureAsync(ModelCatalog.ClapVocab, ct);
        var mergesPath = await registry.EnsureAsync(ModelCatalog.ClapMerges, ct);

        await _textGate.WaitAsync(ct);
        try
        {
            if (_tokenizer is null)
            {
                using var vocab = File.OpenRead(vocabPath);
                using var merges = File.OpenRead(mergesPath);
                _tokenizer = CodeGenTokenizer.Create(vocab, merges, addPrefixSpace: false);
            }
            _textSession ??= new InferenceSession(modelPath);
            _textReaper ??= new Timer(_ => ReleaseText());
            _textReaper.Change(TextIdle, Timeout.InfiniteTimeSpan);

            var ids = _tokenizer.EncodeToIds(text).Take(MaxTokens - 2).Select(id => (long)id).ToList();
            long[] tokens = [Bos, .. ids, Eos];
            using var results = _textSession.Run(
                [NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(tokens, [1, tokens.Length]))]);
            var embedding = Normalise(results[0].AsTensor<float>().ToArray());
            if (_textCache.Count > 256)
            {
                _textCache.Clear();
            }
            _textCache[text] = embedding;
            return embedding;
        }
        finally
        {
            _textGate.Release();
        }
    }

    /// <summary>The 10 s windows the audio tower sees: a short clip repeated to fill one ("repeatpad"),
    /// a long one cropped evenly from start to end, at most <see cref="MaxChunks"/> times.</summary>
    internal static List<float[]> Chunks(float[] mono)
    {
        if (mono.Length == 0)
        {
            return [new float[ChunkSamples]];
        }
        if (mono.Length <= ChunkSamples)
        {
            var padded = new float[ChunkSamples];
            var repeats = ChunkSamples / mono.Length;
            for (var r = 0; r < repeats; r++)
            {
                mono.CopyTo(padded, r * mono.Length);
            }
            return [padded];
        }
        var count = Math.Min(MaxChunks, (int)Math.Ceiling(mono.Length / (double)ChunkSamples));
        var slack = mono.Length - ChunkSamples;
        return [.. Enumerable.Range(0, count).Select(i =>
            mono.AsSpan((int)((long)slack * i / Math.Max(1, count - 1)), ChunkSamples).ToArray())];
    }

    /// <summary>One chunk as the model's input: row-major [<see cref="Frames"/>, <see cref="Mels"/>] dB.</summary>
    internal static float[] Features(float[] chunk)
    {
        var magnitudes = MelSpectrogram.StftMagnitudes(chunk, NFft, Hop);
        var bank = MelBank.Value;
        var features = new float[Frames * Mels];
        Parallel.For(0, Frames, frame =>
        {
            var power = magnitudes[frame];
            for (var k = 0; k < power.Length; k++)
            {
                power[k] *= power[k];
            }
            for (var m = 0; m < Mels; m++)
            {
                features[(frame * Mels) + m] = 10f * MathF.Log10(MathF.Max(MelSpectrogram.Project(power, bank[m]), 1e-10f));
            }
        });
        return features;
    }

    /// <summary>Cosine of two unit vectors.</summary>
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        var sum = 0.0;
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static float[] Normalise(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        return vector;
    }

    private void ReleaseText()
    {
        if (!_textGate.Wait(0))
        {
            _textReaper?.Change(TextIdle, Timeout.InfiniteTimeSpan); // mid-search: try again later
            return;
        }
        try
        {
            _textSession?.Dispose();
            _textSession = null;
        }
        finally
        {
            _textGate.Release();
        }
    }

    public void Dispose()
    {
        _textReaper?.Dispose();
        _textSession?.Dispose();
        _textGate.Dispose();
    }
}
