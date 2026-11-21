using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hpd20Ctrl
{
    /// <summary>Converts a segment into the 16-bit interleaved PCM the audio device expects.</summary>
    public static class PlaybackBuffer
    {
        /// <summary>16-bit little-endian samples for [start, end), interleaved, clipped to full scale.</summary>
        public static short[] ToPcm16(Segment s, int start, int end)
        {
            if (s == null || s.Frames == 0) return new short[0];
            start = Math.Max(0, Math.Min(s.Frames, start));
            end = Math.Max(start, Math.Min(s.Frames, end));
            int channels = Math.Max(1, s.ChannelCount);
            short[] buffer = new short[(end - start) * channels];
            int at = 0;
            for (int i = start; i < end; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float v = s.Channels[c][i];
                    if (v > 1f) v = 1f;
                    if (v < -1f) v = -1f;
                    buffer[at++] = (short)Math.Round(v * 32767f);
                }
            }
            return buffer;
        }

        /// <summary>
        /// Applies a short fade at the start and end of a buffer so playback does not click when it
        /// begins or stops part way through a waveform.
        /// </summary>
        public static void FadeEdges(short[] pcm, int channels, int rate, double milliseconds)
        {
            if (pcm == null || channels <= 0) return;
            int frames = pcm.Length / channels;
            int n = (int)Math.Min(frames / 2, Math.Max(1, rate * milliseconds / 1000.0));
            if (n < 2) return;
            for (int i = 0; i < n; i++)
            {
                float g = (float)i / n;
                for (int c = 0; c < channels; c++) pcm[i * channels + c] = (short)(pcm[i * channels + c] * g);
            }
            for (int i = 0; i < n; i++)
            {
                float g = (float)i / n;
                int idx = (frames - 1 - i) * channels;
                for (int c = 0; c < channels; c++) pcm[idx + c] = (short)(pcm[idx + c] * g);
            }
        }
    }

    /// <summary>
    /// Streaming waveOut player. Keeps several buffers queued with the device at all times, because a
    /// device that runs out of data between buffers produces a click - the single-buffer version of this
    /// class audibly skipped every chunk.
    /// </summary>
    public sealed class WavePlayer : IDisposable
    {
        private const int WAVE_MAPPER = -1;
        private const int CALLBACK_EVENT = 0x00050000;
        private const int WHDR_DONE = 0x00000001;

        private const int QueueDepth = 4;          // buffers kept in flight
        private const int ChunkMs = 80;            // ~320 ms of look-ahead

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateEvent(IntPtr a, bool manual, bool initial, string name);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);

        private Thread _thread;
        private volatile bool _stop;
        private IntPtr _device = IntPtr.Zero;
        private long _writtenFrames;
        private long _inFlightFrames;
        private volatile int _rate = 44100;
        private volatile int _channels = 1;
        private readonly object _gate = new object();

        /// <summary>Completion timestamps in milliseconds, for diagnostics.</summary>
        public readonly List<double> CompletionTimes = new List<double>();

        public bool IsPlaying { get { return _thread != null && _thread.IsAlive; } }
        /// <summary>Frames the device has finished playing (written minus still queued).</summary>
        public long PlayedSamples
        {
            get { return Interlocked.Read(ref _writtenFrames) - Interlocked.Read(ref _inFlightFrames); }
        }
        public int Rate { get { return _rate; } }
        public int Channels { get { return _channels; } }

        /// <summary>Starts playing PCM from the beginning. Any previous playback is stopped first.</summary>
        public void Play(short[] pcm, int rate, int channels)
        {
            Stop();
            if (pcm == null || pcm.Length == 0) return;
            _rate = rate;
            _channels = channels;
            _stop = false;
            Interlocked.Exchange(ref _writtenFrames, 0);
            Interlocked.Exchange(ref _inFlightFrames, 0);
            lock (CompletionTimes) CompletionTimes.Clear();
            _thread = new Thread(delegate () { Run(pcm, rate, channels); });
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            Thread t = _thread;
            _thread = null;
            if (t != null && t.IsAlive) t.Join(2000);
            CloseDevice();
        }

        private void CloseDevice()
        {
            lock (_gate)
            {
                if (_device == IntPtr.Zero) return;
                try { waveOutReset(_device); } catch { }
                try { waveOutClose(_device); } catch { }
                _device = IntPtr.Zero;
            }
        }

        private void Run(short[] pcm, int rate, int channels)
        {
            WAVEFORMATEX fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1;                     // PCM
            fmt.nChannels = (ushort)channels;
            fmt.nSamplesPerSec = (uint)rate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (ushort)(channels * 2);
            fmt.nAvgBytesPerSec = (uint)(rate * channels * 2);
            fmt.cbSize = 0;

            int totalFrames = pcm.Length / channels;
            int framesPerChunk = Math.Max(256, rate * ChunkMs / 1000);
            int bytesPerFrame = channels * 2;
            int maxBytes = framesPerChunk * bytesPerFrame;
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));

            IntPtr evt = CreateEvent(IntPtr.Zero, false, false, null);
            IntPtr[] data = new IntPtr[QueueDepth];
            IntPtr[] hdr = new IntPtr[QueueDepth];
            bool[] prepared = new bool[QueueDepth];
            bool[] queued = new bool[QueueDepth];
            int[] slotFrames = new int[QueueDepth];
            IntPtr device = IntPtr.Zero;

            try
            {
                for (int i = 0; i < QueueDepth; i++)
                {
                    data[i] = Marshal.AllocHGlobal(maxBytes);
                    hdr[i] = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(new WAVEHDR(), hdr[i], false);
                }

                if (waveOutOpen(out device, WAVE_MAPPER, ref fmt, evt, IntPtr.Zero, CALLBACK_EVENT) != 0) return;
                lock (_gate) { _device = device; }

                int nextFrame = 0;
                DateTime start = DateTime.UtcNow;

                // fills a slot and hands it to the device; the header is prepared for the exact length
                Action<int> fill = delegate(int slot)
                {
                    int frames = Math.Min(framesPerChunk, totalFrames - nextFrame);
                    if (frames <= 0) return;
                    int count = frames * channels;
                    int bytes = frames * bytesPerFrame;

                    Marshal.Copy(pcm, nextFrame * channels, data[slot], count);

                    if (prepared[slot]) { waveOutUnprepareHeader(device, hdr[slot], hdrSize); prepared[slot] = false; }

                    WAVEHDR h = new WAVEHDR();
                    h.lpData = data[slot];
                    h.dwBufferLength = (uint)bytes;
                    h.dwFlags = 0;
                    Marshal.StructureToPtr(h, hdr[slot], false);
                    if (waveOutPrepareHeader(device, hdr[slot], hdrSize) != 0) return;
                    prepared[slot] = true;

                    if (waveOutWrite(device, hdr[slot], hdrSize) != 0) return;
                    queued[slot] = true;
                    slotFrames[slot] = frames;
                    nextFrame += frames;
                    Interlocked.Add(ref _writtenFrames, frames);
                    Interlocked.Add(ref _inFlightFrames, frames);
                };

                // prime the queue so the device always has work waiting
                for (int i = 0; i < QueueDepth && nextFrame < totalFrames; i++) fill(i);

                while (!_stop)
                {
                    bool anyQueued = false;
                    for (int i = 0; i < QueueDepth; i++) if (queued[i]) { anyQueued = true; break; }
                    if (!anyQueued) break;

                    WaitForSingleObject(evt, 15);

                    for (int i = 0; i < QueueDepth && !_stop; i++)
                    {
                        if (!queued[i]) continue;
                        WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdr[i], typeof(WAVEHDR));
                        if ((h.dwFlags & WHDR_DONE) == 0) continue;

                        queued[i] = false;
                        Interlocked.Add(ref _inFlightFrames, -slotFrames[i]);
                        lock (CompletionTimes)
                            CompletionTimes.Add((DateTime.UtcNow - start).TotalMilliseconds);
                        slotFrames[i] = 0;

                        if (nextFrame < totalFrames) fill(i);
                    }
                }

                // let the last buffers drain before the caller sees "finished"
                DateTime drainStart = DateTime.UtcNow;
                while (!_stop && (DateTime.UtcNow - drainStart).TotalMilliseconds < ChunkMs * QueueDepth * 2)
                {
                    bool anyQueued2 = false;
                    for (int i = 0; i < QueueDepth; i++) if (queued[i]) { anyQueued2 = true; break; }
                    if (!anyQueued2) break;
                    WaitForSingleObject(evt, 15);
                    for (int i = 0; i < QueueDepth; i++)
                    {
                        if (!queued[i]) continue;
                        WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdr[i], typeof(WAVEHDR));
                        if ((h.dwFlags & WHDR_DONE) == 0) continue;
                        queued[i] = false;
                        Interlocked.Add(ref _inFlightFrames, -slotFrames[i]);
                        slotFrames[i] = 0;
                    }
                }
            }
            finally
            {
                if (device != IntPtr.Zero)
                {
                    waveOutReset(device);
                    for (int i = 0; i < QueueDepth; i++)
                        if (prepared[i]) { try { waveOutUnprepareHeader(device, hdr[i], hdrSize); } catch { } }
                }
                CloseDevice();
                for (int i = 0; i < QueueDepth; i++)
                {
                    if (hdr[i] != IntPtr.Zero) Marshal.FreeHGlobal(hdr[i]);
                    if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
                }
                if (evt != IntPtr.Zero) CloseHandle(evt);
            }
        }
        public void Dispose() { Stop(); }
    }
}
