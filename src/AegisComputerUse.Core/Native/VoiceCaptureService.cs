using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AegisComputerUse.Core.Native;

/// <summary>
/// Captures 16 kHz / 16-bit / mono PCM audio strictly in RAM using Windows Multimedia API (winmm.dll).
/// Never writes temporary audio files to disk.
/// </summary>
public sealed class VoiceCaptureService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferMs = 100;
    private const int BufferSize = SampleRate * (BitsPerSample / 8) * Channels * BufferMs / 1000; // 3200 bytes
    private const int BufferCount = 4;
    private const int MaxRecordingBytes = SampleRate * 2 * 60; // Hard cap: 60 seconds (~1.92 MB)

    private readonly Lock _gate = new();
    private IntPtr _hWaveIn;
    private AutoResetEvent? _audioEvent;
    private CancellationTokenSource? _loopCts;
    private Task? _captureTask;
    private MemoryStream? _pcmStream;
    private readonly List<(GCHandle dataHandle, GCHandle hdrHandle, IntPtr hdrPtr)> _buffers = [];
    private bool _isRecording;
    private bool _isSpeaking;
    private int _silenceMs;
    private MemoryStream? _liveUtteranceStream;

    public bool ContinuousMode { get; set; }
    public double SpeechRmsThreshold { get; set; } = 700;
    public int SilenceTimeoutMs { get; set; } = 1200;
    public int MinUtteranceDurationMs { get; set; } = 500;

    public event Action<byte[]>? UtteranceCompleted;
    public event Action<bool>? SpeechActivityChanged;

    public bool IsRecording
    {
        get { lock (_gate) return _isRecording; }
    }

    public static bool HasMicrophone() => Win32Interop.waveInGetNumDevs() > 0;

    public void StartRecording()
    {
        lock (_gate)
        {
            if (_isRecording) return;
            if (!HasMicrophone())
                throw new InvalidOperationException("No audio input microphone detected.");

            var format = new Win32Interop.WAVEFORMATEX
            {
                WFormatTag = 1, // PCM
                NChannels = Channels,
                NSamplesPerSec = SampleRate,
                WBitsPerSample = BitsPerSample,
                NBlockAlign = Channels * (BitsPerSample / 8),
                NAvgBytesPerSec = SampleRate * Channels * (BitsPerSample / 8),
                CbSize = 0
            };

            _audioEvent = new AutoResetEvent(false);
            uint res = Win32Interop.waveInOpen(
                out _hWaveIn,
                0xFFFFFFFF, // WAVE_MAPPER
                ref format,
                _audioEvent.SafeWaitHandle.DangerousGetHandle(),
                IntPtr.Zero,
                Win32Interop.CALLBACK_EVENT);

            if (res != 0 || _hWaveIn == IntPtr.Zero)
            {
                _audioEvent.Dispose();
                _audioEvent = null;
                throw new InvalidOperationException($"Failed to open microphone (waveInOpen code {res}).");
            }

            _pcmStream = new MemoryStream();
            _buffers.Clear();

            for (int i = 0; i < BufferCount; i++)
            {
                byte[] data = new byte[BufferSize];
                var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
                var hdr = new Win32Interop.WAVEHDR
                {
                    LpData = dataHandle.AddrOfPinnedObject(),
                    DwBufferLength = BufferSize
                };
                var hdrHandle = GCHandle.Alloc(hdr, GCHandleType.Pinned);
                IntPtr hdrPtr = hdrHandle.AddrOfPinnedObject();

                uint prep = Win32Interop.waveInPrepareHeader(_hWaveIn, hdrPtr, (uint)Marshal.SizeOf<Win32Interop.WAVEHDR>());
                uint add = Win32Interop.waveInAddBuffer(_hWaveIn, hdrPtr, (uint)Marshal.SizeOf<Win32Interop.WAVEHDR>());
                if (prep != 0 || add != 0)
                {
                    CleanupNativeBuffers();
                    throw new InvalidOperationException($"Failed to prepare audio buffer (prep={prep}, add={add}).");
                }

                _buffers.Add((dataHandle, hdrHandle, hdrPtr));
            }

            uint startRes = Win32Interop.waveInStart(_hWaveIn);
            if (startRes != 0)
            {
                CleanupNativeBuffers();
                throw new InvalidOperationException($"Failed to start audio capture (code {startRes}).");
            }

            _isRecording = true;
            _loopCts = new CancellationTokenSource();
            _captureTask = Task.Run(() => CaptureLoop(_loopCts.Token));
        }
    }

    public void StopRecording()
    {
        _ = StopAndBuildWav();
    }

    public byte[] StopAndBuildWav()
    {
        Task? loop;
        lock (_gate)
        {
            if (!_isRecording) return [];
            _isRecording = false;
            _loopCts?.Cancel();
            _audioEvent?.Set();
            loop = _captureTask;
        }

        try { loop?.Wait(1500); } catch { /* ignore shutdown race */ }

        lock (_gate)
        {
            try
            {
                if (_hWaveIn != IntPtr.Zero)
                {
                    Win32Interop.waveInStop(_hWaveIn);
                    Win32Interop.waveInReset(_hWaveIn);
                }

                DrainCompletedBuffers();

                byte[] pcm = _pcmStream?.ToArray() ?? [];
                try
                {
                    return BuildWavContainer(pcm);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pcm);
                }
            }
            finally
            {
                CleanupNativeBuffers();
            }
        }
    }

    private void CaptureLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _audioEvent?.WaitOne(200);
            lock (_gate)
            {
                if (!_isRecording) break;
                DrainCompletedBuffers();
            }
        }
    }

    private void DrainCompletedBuffers()
    {
        if (_hWaveIn == IntPtr.Zero || _pcmStream is null) return;

        foreach (var (dataHandle, _, hdrPtr) in _buffers)
        {
            var hdr = Marshal.PtrToStructure<Win32Interop.WAVEHDR>(hdrPtr);
            if ((hdr.DwFlags & Win32Interop.WHDR_DONE) == 0) continue;

            if (hdr.DwBytesRecorded > 0)
            {
                byte[] buf = (byte[])dataHandle.Target!;
                int recorded = (int)hdr.DwBytesRecorded;

                if (!ContinuousMode)
                {
                    if (_pcmStream.Length + recorded <= MaxRecordingBytes)
                    {
                        _pcmStream.Write(buf, 0, recorded);
                    }
                }
                else
                {
                    double rms = CalculateRms(buf, 0, recorded);
                    bool speechDetected = rms >= SpeechRmsThreshold;

                    if (speechDetected)
                    {
                        if (!_isSpeaking)
                        {
                            _isSpeaking = true;
                            SpeechActivityChanged?.Invoke(true);
                        }
                        _silenceMs = 0;
                        _liveUtteranceStream ??= new MemoryStream();
                        if (_liveUtteranceStream.Length + recorded <= MaxRecordingBytes)
                        {
                            _liveUtteranceStream.Write(buf, 0, recorded);
                        }
                    }
                    else if (_isSpeaking)
                    {
                        _silenceMs += BufferMs;
                        _liveUtteranceStream ??= new MemoryStream();
                        if (_liveUtteranceStream.Length + recorded <= MaxRecordingBytes)
                        {
                            _liveUtteranceStream.Write(buf, 0, recorded);
                        }

                        if (_silenceMs >= SilenceTimeoutMs)
                        {
                            int minBytes = SampleRate * (BitsPerSample / 8) * Channels * MinUtteranceDurationMs / 1000;
                            if (_liveUtteranceStream.Length >= minBytes)
                            {
                                byte[] pcm = _liveUtteranceStream.ToArray();
                                byte[] wav = BuildWavContainer(pcm);
                                CryptographicOperations.ZeroMemory(pcm);
                                UtteranceCompleted?.Invoke(wav);
                            }

                            _liveUtteranceStream.Dispose();
                            _liveUtteranceStream = null;
                            _isSpeaking = false;
                            _silenceMs = 0;
                            SpeechActivityChanged?.Invoke(false);
                        }
                    }
                }

                CryptographicOperations.ZeroMemory(buf);
            }

            if (_isRecording)
            {
                Win32Interop.waveInAddBuffer(_hWaveIn, hdrPtr, (uint)Marshal.SizeOf<Win32Interop.WAVEHDR>());
            }
        }
    }

    public static double CalculateRms(byte[] buffer, int offset, int count)
    {
        if (count < 2) return 0;
        long sum = 0;
        int samples = count / 2;
        for (int i = 0; i < samples; i++)
        {
            short sample = (short)(buffer[offset + i * 2] | (buffer[offset + i * 2 + 1] << 8));
            sum += (long)sample * sample;
        }
        return Math.Sqrt((double)sum / samples);
    }

    internal static byte[] BuildWavContainer(byte[] pcmData)
    {
        int dataLen = pcmData.Length;
        byte[] wav = new byte[44 + dataLen];
        using var ms = new MemoryStream(wav);
        using var bw = new BinaryWriter(ms, Encoding.ASCII);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataLen);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16); // PCM chunk size
        bw.Write((short)1); // PCM format
        bw.Write((short)Channels);
        bw.Write(SampleRate);
        bw.Write(SampleRate * Channels * (BitsPerSample / 8));
        bw.Write((short)(Channels * (BitsPerSample / 8)));
        bw.Write((short)BitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataLen);
        bw.Write(pcmData);
        return wav;
    }

    private void CleanupNativeBuffers()
    {
        foreach (var (dataHandle, hdrHandle, hdrPtr) in _buffers)
        {
            try
            {
                if (_hWaveIn != IntPtr.Zero)
                    Win32Interop.waveInUnprepareHeader(_hWaveIn, hdrPtr, (uint)Marshal.SizeOf<Win32Interop.WAVEHDR>());
            }
            catch { }

            if (dataHandle.Target is byte[] buf)
                CryptographicOperations.ZeroMemory(buf);

            if (dataHandle.IsAllocated) dataHandle.Free();
            if (hdrHandle.IsAllocated) hdrHandle.Free();
        }
        _buffers.Clear();

        if (_hWaveIn != IntPtr.Zero)
        {
            Win32Interop.waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }

        if (_pcmStream is not null)
        {
            if (_pcmStream.TryGetBuffer(out ArraySegment<byte> seg))
                CryptographicOperations.ZeroMemory(seg);
            _pcmStream.Dispose();
            _pcmStream = null;
        }

        if (_liveUtteranceStream is not null)
        {
            if (_liveUtteranceStream.TryGetBuffer(out ArraySegment<byte> liveSeg))
                CryptographicOperations.ZeroMemory(liveSeg);
            _liveUtteranceStream.Dispose();
            _liveUtteranceStream = null;
        }
        _isSpeaking = false;
        _silenceMs = 0;

        _loopCts?.Dispose();
        _loopCts = null;
        _audioEvent?.Dispose();
        _audioEvent = null;
        _captureTask = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _isRecording = false;
            _loopCts?.Cancel();
            _audioEvent?.Set();
            CleanupNativeBuffers();
        }
    }
}
