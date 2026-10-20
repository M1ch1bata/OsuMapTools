using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using NVorbis;

namespace Slim
{
    /// <summary>Decodes mp3/ogg/wav, time-stretches with pitch preserved and encodes a 192 kbps mp3.</summary>
    public static class AudioProcessor
    {
        public const int TargetSampleRate = 44100;
        public const int TargetBitrate = 192000;

        public static void CreateRateAdjustedMp3(string sourcePath, string destPath, double rate, bool preservePitch,
            System.Threading.CancellationToken token = default(System.Threading.CancellationToken))
        {
            if (Math.Abs(rate - 1.0) < 1e-9)
                throw new ArgumentException("rate must differ from 1.0");

            token.ThrowIfCancellationRequested();
            ISampleProvider source = OpenSource(sourcePath);
            if (token.CanBeCanceled) source = new CancellableSampleProvider(source, token);

            if (source.WaveFormat.Channels == 1)
                source = new MonoToStereoSampleProvider(source);

            if (source.WaveFormat.SampleRate != TargetSampleRate)
                source = new WdlResamplingSampleProvider(source, TargetSampleRate);

            ISampleProvider processed = preservePitch
                ? (ISampleProvider)new SoundTouchSampleProvider(source, rate)
                : new SpeedUpSampleProvider(source, rate);
            var wave16 = new SampleToWaveProvider16(processed);

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = destPath + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp3";
            try
            {
                MediaFoundationEncoder.EncodeToMp3(wave16, temp, TargetBitrate);
                token.ThrowIfCancellationRequested();
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(temp, destPath);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private sealed class CancellableSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _inner;
            private readonly System.Threading.CancellationToken _token;

            public CancellableSampleProvider(ISampleProvider inner, System.Threading.CancellationToken token)
            {
                _inner = inner;
                _token = token;
                WaveFormat = inner.WaveFormat;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                _token.ThrowIfCancellationRequested();
                return _inner.Read(buffer, offset, count);
            }
        }

        private static ISampleProvider OpenSource(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ogg")
                return new VorbisSampleProvider(path);

            try
            {
                return new MediaFoundationReader(path).ToSampleProvider();
            }
            catch
            {
                if (ext == ".mp3") return new Mp3FileReader(path).ToSampleProvider();
                if (ext == ".wav") return new WaveFileReader(path).ToSampleProvider();
                throw;
            }
        }

        private sealed class VorbisSampleProvider : ISampleProvider
        {
            private readonly VorbisReader _reader;

            public VorbisSampleProvider(string path)
            {
                _reader = new VorbisReader(path);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                return _reader.ReadSamples(buffer, offset, count);
            }
        }

        /// <summary>Naive resample: keeps the nominal sample rate, so tempo and pitch both change by rate.</summary>
        private sealed class SpeedUpSampleProvider : ISampleProvider
        {
            private readonly WdlResamplingSampleProvider _inner;

            public SpeedUpSampleProvider(ISampleProvider source, double rate)
            {
                int sampleRate = source.WaveFormat.SampleRate;
                // Resample to sampleRate / rate and keep declaring sampleRate: playing the frames
                // back at the nominal rate shortens the duration by `rate` (tempo+pitch change).
                int target = (int)Math.Round(sampleRate / rate);
                if (target < 8000) target = 8000;
                if (target > 192000) target = 192000;
                _inner = new WdlResamplingSampleProvider(source, target);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, source.WaveFormat.Channels);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }
        }

        /// <summary>Pulls from an ISampleProvider, runs SoundTouch tempo change and yields float samples.</summary>
        private sealed class SoundTouchSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly SoundTouchProcessor _st;
            private readonly int _channels;
            private readonly float[] _input;
            private readonly float[] _output;
            private readonly Queue<float> _pending = new Queue<float>();
            private bool _sourceDone;
            private bool _flushed;

            public SoundTouchSampleProvider(ISampleProvider source, double rate)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
                WaveFormat = source.WaveFormat;

                _st = new SoundTouchProcessor
                {
                    Channels = _channels,
                    SampleRate = source.WaveFormat.SampleRate,
                    TempoChange = (rate - 1.0) * 100.0,
                };

                _input = new float[_channels * 4096];
                _output = new float[_channels * 16384];
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int written = 0;
                while (written < count)
                {
                    if (_pending.Count == 0 && !Fill())
                        break;

                    int take = Math.Min(count - written, _pending.Count);
                    for (int i = 0; i < take; i++) buffer[offset + written + i] = _pending.Dequeue();
                    written += take;
                }
                return written;
            }

            private bool Fill()
            {
                while (true)
                {
                    if (!_sourceDone)
                    {
                        int read = _source.Read(_input, 0, _input.Length);
                        if (read > 0)
                        {
                            _st.PutSamples(_input, read / _channels);
                            Drain();
                            if (_pending.Count > 0) return true;
                            continue;
                        }
                        _sourceDone = true;
                    }

                    if (!_flushed)
                    {
                        _flushed = true;
                        _st.Flush();
                        Drain();
                        return _pending.Count > 0;
                    }

                    return _pending.Count > 0;
                }
            }

            private void Drain()
            {
                while (true)
                {
                    int got = _st.ReceiveSamples(_output, _output.Length / _channels);
                    if (got <= 0) break;
                    for (int i = 0; i < got * _channels; i++) _pending.Enqueue(_output[i]);
                }
            }
        }
    }
}
