using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Auralistix
{
    /// <summary>
    /// Один общий микшер (ISampleProvider) -> памп в буферы -> WasapiOut на "микрофон" (вирт. кабель)
    /// + опционально второй WasapiOut на мониторинг.
    ///
    /// ВАЖНО: ВСЕ источники (файлы, пик, TTS) должны попадать в этот микшер через AddToMixer(...)
    /// </summary>
    public sealed class AudioRouter : IDisposable
    {
        private readonly object _mixerLock = new object();

        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _micRenderDevice;
        private MMDevice? _monitorRenderDevice;

        private WasapiOut? _micOut;
        private WasapiOut? _monitorOut;

        private BufferedWaveProvider? _micBuffer;
        private BufferedWaveProvider? _monitorBuffer;

        private MixingSampleProvider? _mixer;
        private IWaveProvider? _mixerWaveProvider;

        private CancellationTokenSource? _pumpCts;
        private Task? _pumpTask;

        private bool _monitorEnabled;
        private int _latencyMs;

        public WaveFormat? MixWaveFormat => _mixer?.WaveFormat;

        public AudioRouter(int latencyMs = 60)
        {
            _latencyMs = Math.Max(20, latencyMs);
        }

        /// <summary>
        /// Список render-устройств (куда можно "выводить"). Для виртуального микрофона нужен VB-Cable/Voicemeeter и т.п.
        /// </summary>
        public static List<(string Id, string Name)> GetRenderDevices()
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                             .Select(d => (d.ID, d.FriendlyName))
                             .ToList();
        }

        /// <summary>
        /// Запуск роутинга: основной выход в виртуальный кабель (render-девайс).
        /// monitorDeviceId - опционально (наушники/колонки).
        /// </summary>
        public void Start(string micRenderDeviceId, bool enableMonitor, string? monitorDeviceId = null)
        {
            Stop();

            _enumerator = new MMDeviceEnumerator();
            _micRenderDevice = _enumerator.GetDevice(micRenderDeviceId);

            // Берём формат движка Windows (mix format) для выбранного устройства
            var mix = _micRenderDevice.AudioClient.MixFormat;

            // Микшер делаем float (это удобнее для SampleProviders)
            // Обычно в shared mode у WASAPI движок всё равно float.
            var sampleRate = mix.SampleRate;
            var channels = mix.Channels;

            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels))
            {
                ReadFully = true
            };

            _mixerWaveProvider = new SampleToWaveProvider(_mixer);

            _micBuffer = new BufferedWaveProvider(_mixerWaveProvider.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };

            _micOut = new WasapiOut(_micRenderDevice, AudioClientShareMode.Shared, true, _latencyMs);
            _micOut.Init(_micBuffer);

            _monitorEnabled = false;
            if (enableMonitor)
            {
                EnableMonitor(true, monitorDeviceId);
            }

            // запускаем памп ДО Play, чтобы не было пустых буферов в первые миллисекунды
            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpLoop(_pumpCts.Token));

            _micOut.Play();
            if (_monitorEnabled)
                _monitorOut?.Play();
        }

        public void EnableMonitor(bool enable, string? monitorDeviceId = null)
        {
            if (_mixerWaveProvider == null || _enumerator == null)
            {
                _monitorEnabled = false;
                return;
            }

            if (!enable)
            {
                _monitorEnabled = false;

                try { _monitorOut?.Stop(); } catch { /* ignore */ }
                try { _monitorOut?.Dispose(); } catch { /* ignore */ }
                _monitorOut = null;

                _monitorBuffer = null;
                _monitorRenderDevice = null;
                return;
            }

            // Включаем монитор
            if (string.IsNullOrWhiteSpace(monitorDeviceId))
            {
                // дефолтное устройство воспроизведения
                _monitorRenderDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else
            {
                _monitorRenderDevice = _enumerator.GetDevice(monitorDeviceId);
            }

            _monitorBuffer = new BufferedWaveProvider(_mixerWaveProvider.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };

            // ВАЖНО: мы ИНИЦИАЛИЗИРУЕМ монитор тем же форматом, что и микшер.
            // Если Windows/драйвер не примет этот формат - будет исключение (тогда выставь одинаковый формат 48k/44.1k в свойствах устройств).
            _monitorOut = new WasapiOut(_monitorRenderDevice, AudioClientShareMode.Shared, true, _latencyMs);
            _monitorOut.Init(_monitorBuffer);

            _monitorEnabled = true;

            try
            {
                if (_micOut != null) // если уже запущено
                    _monitorOut.Play();
            }
            catch
            {
                // если Play не удалось, откатываем монитор
                _monitorEnabled = false;
                try { _monitorOut.Stop(); } catch { }
                try { _monitorOut.Dispose(); } catch { }
                _monitorOut = null;
                _monitorBuffer = null;
            }
        }

        public void Stop()
        {
            try { _pumpCts?.Cancel(); } catch { }
            try { _pumpTask?.Wait(300); } catch { /* ignore */ }

            _pumpTask = null;
            _pumpCts?.Dispose();
            _pumpCts = null;

            try { _micOut?.Stop(); } catch { }
            try { _monitorOut?.Stop(); } catch { }

            try { _micOut?.Dispose(); } catch { }
            try { _monitorOut?.Dispose(); } catch { }

            _micOut = null;
            _monitorOut = null;

            _micBuffer = null;
            _monitorBuffer = null;

            _mixerWaveProvider = null;
            _mixer = null;

            _micRenderDevice = null;
            _monitorRenderDevice = null;

            try { _enumerator?.Dispose(); } catch { }
            _enumerator = null;

            _monitorEnabled = false;
        }

        /// <summary>
        /// Проиграть аудиофайл через микшер (попадёт в "микрофон" и в монитор, если включён).
        /// </summary>
        public void PlayFile(string path, float volume = 1f, float pan = 0f)
        {
            if (_mixer == null) return;
            if (!File.Exists(path)) return;

            var reader = new AudioFileReader(path) { Volume = Clamp01(volume) };

            ISampleProvider sp = reader.ToSampleProvider();
            sp = ConvertToMixerFormat(sp);

            // PanningSampleProvider работает только со стерео
            if (sp.WaveFormat.Channels == 2 && Math.Abs(pan) > 0.0001f)
            {
                var panner = new PanningSampleProvider(sp) { Pan = Clamp(pan, -1f, 1f) };
                sp = panner;
            }

            AddToMixer(new AutoDisposeSampleProvider(sp, reader));
        }

        /// <summary>
        /// Пик (button5) — НЕ SystemSounds/Console.Beep, а сигнал в микшер.
        /// </summary>
        public void PlayPeak(float gain = 0.20f, int frequencyHz = 1600, int durationMs = 35)
        {
            if (_mixer == null) return;

            var fmt = _mixer.WaveFormat;

            // Генерация короткого синуса прямо в формате микшера
            var beep = new SineBeepSampleProvider(fmt, frequencyHz, durationMs, gain, fadeInMs: 2, fadeOutMs: 7);
            AddToMixer(beep);
        }

        /// <summary>
        /// Озвучка TTS в микшер (а значит — и в "микрофон"), без проигрывания через дефолтный девайс Windows.
        /// </summary>
        public async Task SpeakAsync(string text, string? voiceName = null, int rate = 0, int volume = 100)
        {
            if (_mixer == null) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            // Синтез лучше делать не на UI-потоке
            await Task.Run(() =>
            {
                using var synth = new SpeechSynthesizer();

                try
                {
                    if (!string.IsNullOrWhiteSpace(voiceName))
                        synth.SelectVoice(voiceName);
                }
                catch
                {
                    // если голос не найден — просто используем дефолтный
                }

                synth.Rate = Clamp(rate, -10, 10);
                synth.Volume = Clamp(volume, 0, 100);

                using var ms = new MemoryStream();
                synth.SetOutputToWaveStream(ms);
                synth.Speak(text);
                synth.SetOutputToNull();

                ms.Position = 0;

                // WaveFileReader читает WAV из памяти
                var reader = new WaveFileReader(ms);
                ISampleProvider sp = reader.ToSampleProvider();
                sp = ConvertToMixerFormat(sp);

                AddToMixer(new AutoDisposeSampleProvider(sp, reader, ms));
            });
        }

        private void AddToMixer(ISampleProvider input)
        {
            if (_mixer == null) return;

            lock (_mixerLock)
            {
                _mixer.AddMixerInput(input);
            }
        }

        private ISampleProvider ConvertToMixerFormat(ISampleProvider input)
        {
            if (_mixer == null) return input;

            var target = _mixer.WaveFormat;
            ISampleProvider sp = input;

            // каналы
            if (sp.WaveFormat.Channels == 1 && target.Channels == 2)
                sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels == 2 && target.Channels == 1)
                sp = new StereoToMonoSampleProvider(sp);

            // sample rate
            if (sp.WaveFormat.SampleRate != target.SampleRate)
                sp = new WdlResamplingSampleProvider(sp, target.SampleRate);

            // если вдруг остались отличия по каналам (редко) — приводим к target.Channels через Multiplexing
            if (sp.WaveFormat.Channels != target.Channels)
            {
                var multi = new MultiplexingSampleProvider(new[] { sp }, target.Channels);
                for (int ch = 0; ch < target.Channels; ch++)
                    multi.ConnectInputToOutput(0, Math.Min(ch, sp.WaveFormat.Channels - 1));
                sp = multi;
            }

            return sp;
        }

        private void PumpLoop(CancellationToken token)
        {
            if (_mixerWaveProvider == null || _micBuffer == null) return;

            var bytesPerSec = _mixerWaveProvider.WaveFormat.AverageBytesPerSecond;
            int targetBuffered = (int)(bytesPerSec * 0.20); // держим ~200мс в очереди
            int chunk = Math.Max(1024, bytesPerSec / 100);  // примерно 10мс блок
            var temp = new byte[chunk];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int needMic = targetBuffered - _micBuffer.BufferedBytes;
                    int needMon = 0;

                    if (_monitorEnabled && _monitorBuffer != null)
                        needMon = targetBuffered - _monitorBuffer.BufferedBytes;

                    int need = _monitorEnabled ? Math.Min(needMic, needMon) : needMic;

                    if (need <= 0)
                    {
                        Thread.Sleep(3);
                        continue;
                    }

                    int read = _mixerWaveProvider.Read(temp, 0, temp.Length);
                    if (read > 0)
                    {
                        _micBuffer.AddSamples(temp, 0, read);
                        if (_monitorEnabled && _monitorBuffer != null)
                            _monitorBuffer.AddSamples(temp, 0, read);
                    }
                    else
                    {
                        Thread.Sleep(2);
                    }
                }
                catch
                {
                    Thread.Sleep(10);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private sealed class SineBeepSampleProvider : ISampleProvider
        {
            public WaveFormat WaveFormat { get; }

            private readonly int _channels;
            private readonly int _sampleRate;

            private readonly int _totalFrames;     // “кадры” = сэмплы на канал
            private readonly int _fadeInFrames;
            private readonly int _fadeOutFrames;

            private readonly double _phaseStep;
            private double _phase;

            private readonly float _gain;

            private int _framePos; // текущий кадр

            public SineBeepSampleProvider(
                WaveFormat format,
                int frequencyHz,
                int durationMs,
                float gain,
                int fadeInMs,
                int fadeOutMs)
            {
                WaveFormat = format;
                _channels = format.Channels;
                _sampleRate = format.SampleRate;

                _gain = gain < 0f ? 0f : (gain > 1f ? 1f : gain);

                _totalFrames = Math.Max(1, (int)Math.Round(_sampleRate * (durationMs / 1000.0)));
                _fadeInFrames = Math.Max(0, (int)Math.Round(_sampleRate * (fadeInMs / 1000.0)));
                _fadeOutFrames = Math.Max(0, (int)Math.Round(_sampleRate * (fadeOutMs / 1000.0)));

                _phaseStep = 2.0 * Math.PI * frequencyHz / _sampleRate;
                _phase = 0.0;
                _framePos = 0;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                // count — количество float-значений (включая все каналы)
                int framesRequested = count / _channels;
                if (framesRequested <= 0) return 0;

                int framesLeft = _totalFrames - _framePos;
                if (framesLeft <= 0) return 0;

                int framesToWrite = Math.Min(framesRequested, framesLeft);
                int samplesToWrite = framesToWrite * _channels;

                int n = offset;

                for (int i = 0; i < framesToWrite; i++)
                {
                    int frameIndex = _framePos + i;

                    float env = 1f;

                    // fade-in
                    if (_fadeInFrames > 0 && frameIndex < _fadeInFrames)
                        env *= (float)frameIndex / _fadeInFrames;

                    // fade-out (последние _fadeOutFrames кадров)
                    int fadeOutStart = _totalFrames - _fadeOutFrames;
                    if (_fadeOutFrames > 0 && frameIndex >= fadeOutStart)
                    {
                        int k = frameIndex - fadeOutStart; // 0..fadeOutFrames-1
                        env *= 1f - ((float)k / _fadeOutFrames);
                    }

                    float sample = (float)(Math.Sin(_phase) * _gain * env);
                    _phase += _phaseStep;
                    if (_phase > 2.0 * Math.PI) _phase -= 2.0 * Math.PI;

                    for (int ch = 0; ch < _channels; ch++)
                        buffer[n++] = sample;
                }

                _framePos += framesToWrite;
                return samplesToWrite;
            }
        }

        private sealed class AutoDisposeSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly IDisposable[] _dispose;
            private bool _disposed;

            public AutoDisposeSampleProvider(ISampleProvider source, params IDisposable[] dispose)
            {
                _source = source;
                _dispose = dispose ?? Array.Empty<IDisposable>();
                WaveFormat = source.WaveFormat;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read == 0) DisposeOnce();
                return read;
            }

            private void DisposeOnce()
            {
                if (_disposed) return;
                _disposed = true;

                foreach (var d in _dispose)
                {
                    try { d?.Dispose(); } catch { /* ignore */ }
                }
            }
        }
    }
}