using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Hpd20Ctrl
{
    /// <summary>
    /// Decodes compressed audio through Windows Media Foundation, so MP3, FLAC, OGG/Vorbis, AAC/M4A,
    /// ALAC and WMA work without shipping any codec or third-party library. The reader is asked for
    /// 16-bit PCM at the file's own rate and channel count, which every Windows audio decoder supports.
    /// </summary>
    internal static class MfAudio
    {
        // ---- GUIDs (values taken from mfapi.h / mfidl.idl)
        private static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");

        private const int MF_VERSION = 0x00020070;
        private const int MFSTARTUP_FULL = 0;
        private const int MF_SOURCE_READER_FIRST_AUDIO_STREAM = -3;
        private const int MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
        private const int MF_SOURCE_READERF_ERROR = 0x1;
        private const int MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;

        // ---- interop
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid key, out int type);
            [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            [PreserveSig] int GetDouble(ref Guid key, out double value);
            [PreserveSig] int GetGUID(ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid key, out int length);
            [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
            [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
            [PreserveSig] int GetBlobSize(ref Guid key, out int size);
            [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
            [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr unknown);
            [PreserveSig] int SetItem(ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            [PreserveSig] int SetUINT64(ref Guid key, long value);
            [PreserveSig] int SetDouble(ref Guid key, double value);
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
            [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes destination);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            // IMFAttributes
            [PreserveSig] int GetItem(ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid key, out int type);
            [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            [PreserveSig] int GetDouble(ref Guid key, out double value);
            [PreserveSig] int GetGUID(ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid key, out int length);
            [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
            [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
            [PreserveSig] int GetBlobSize(ref Guid key, out int size);
            [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
            [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr unknown);
            [PreserveSig] int SetItem(ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            [PreserveSig] int SetUINT64(ref Guid key, long value);
            [PreserveSig] int SetDouble(ref Guid key, double value);
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
            [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes destination);
            // IMFMediaType
            [PreserveSig] int GetMajorType(out Guid majorType);
            [PreserveSig] int IsCompressedFormat(out int compressed);
            [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
            [PreserveSig] int GetRepresentation(ref Guid representation, out IntPtr value);
            [PreserveSig] int FreeRepresentation(ref Guid representation, IntPtr value);
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int currentLength);
            [PreserveSig] int SetCurrentLength(int currentLength);
            [PreserveSig] int GetMaxLength(out int maxLength);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes
            [PreserveSig] int GetItem(ref Guid key, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid key, out int type);
            [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
            [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            [PreserveSig] int GetDouble(ref Guid key, out double value);
            [PreserveSig] int GetGUID(ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid key, out int length);
            [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, IntPtr length);
            [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
            [PreserveSig] int GetBlobSize(ref Guid key, out int size);
            [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
            [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr unknown);
            [PreserveSig] int SetItem(ref Guid key, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid key);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            [PreserveSig] int SetUINT64(ref Guid key, long value);
            [PreserveSig] int SetDouble(ref Guid key, double value);
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
            [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes destination);
            // IMFSample
            [PreserveSig] int GetSampleFlags(out int flags);
            [PreserveSig] int SetSampleFlags(int flags);
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(out int count);
            [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
            [PreserveSig] int RemoveBufferByIndex(int index);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out int length);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(int index, out int selected);
            [PreserveSig] int SetStreamSelection(int index, int selected);
            [PreserveSig] int GetNativeMediaType(int index, int typeIndex, out IMFMediaType type);
            [PreserveSig] int GetCurrentMediaType(int index, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(int index, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
            [PreserveSig] int ReadSample(int index, int flags, out int actualIndex, out int sampleFlags,
                                         out long timestamp, out IMFSample sample);
            [PreserveSig] int Flush(int index);
            [PreserveSig] int GetServiceForStream(int index, ref Guid service, ref Guid riid, out IntPtr obj);
            [PreserveSig] int GetPresentationAttribute(int index, ref Guid attribute, IntPtr value);
        }

        // ---- public API

        /// <summary>True when Media Foundation can be started on this machine.</summary>
        public static bool Available
        {
            get
            {
                try { return MFStartup(MF_VERSION, MFSTARTUP_FULL) == 0; }
                catch { return false; }
            }
        }

        /// <summary>Decodes any file the installed Media Foundation codecs understand into float samples.</summary>
        public static WavData Decode(string path)
        {
            int hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
            if (hr != 0) throw new InvalidOperationException("Media Foundation could not start (0x" + hr.ToString("X8") + ")");

            IMFSourceReader reader = null;
            try
            {
                hr = MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader);
                if (hr != 0 || reader == null)
                    throw new NotSupportedException(Describe(hr, path));

                // the file's own layout, so no resampling happens inside the decoder
                int channels = 2, rate = 44100;
                IMFMediaType native;
                if (reader.GetNativeMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out native) == 0 && native != null)
                {
                    int v;
                    if (native.GetUINT32(ref MfAudio.MF_MT_AUDIO_NUM_CHANNELS_, out v) == 0 && v > 0) channels = v;
                    if (native.GetUINT32(ref MfAudio.MF_MT_AUDIO_SAMPLES_PER_SECOND_, out v) == 0 && v > 0) rate = v;
                    Marshal.ReleaseComObject(native);
                }

                IMFMediaType target;
                hr = MFCreateMediaType(out target);
                if (hr != 0) throw new InvalidOperationException("MFCreateMediaType failed (0x" + hr.ToString("X8") + ")");
                try
                {
                    Guid audio = MFMediaType_Audio, pcm = MFAudioFormat_PCM;
                    target.SetGUID(ref MfAudio.MF_MT_MAJOR_TYPE_, ref audio);
                    target.SetGUID(ref MfAudio.MF_MT_SUBTYPE_, ref pcm);
                    target.SetUINT32(ref MfAudio.MF_MT_AUDIO_NUM_CHANNELS_, channels);
                    target.SetUINT32(ref MfAudio.MF_MT_AUDIO_SAMPLES_PER_SECOND_, rate);
                    target.SetUINT32(ref MfAudio.MF_MT_AUDIO_BITS_PER_SAMPLE_, 16);
                    target.SetUINT32(ref MfAudio.MF_MT_AUDIO_BLOCK_ALIGNMENT_, channels * 2);
                    target.SetUINT32(ref MfAudio.MF_MT_AUDIO_AVG_BYTES_PER_SECOND_, rate * channels * 2);
                    target.SetUINT32(ref MfAudio.MF_MT_ALL_SAMPLES_INDEPENDENT_, 1);

                    hr = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, target);
                    if (hr != 0)
                        throw new InvalidOperationException("the decoder cannot produce 16-bit PCM (0x" + hr.ToString("X8") + ")");
                }
                finally { Marshal.ReleaseComObject(target); }

                // pull samples
                List<short> interleaved = new List<short>(rate * channels * 4);
                while (true)
                {
                    int actual, flags;
                    long ts;
                    IMFSample sample;
                    hr = reader.ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out actual, out flags, out ts, out sample);
                    if (hr != 0) throw new InvalidOperationException("read failed (0x" + hr.ToString("X8") + ")");
                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                    if ((flags & MF_SOURCE_READERF_ERROR) != 0) throw new InvalidOperationException("decoder reported an error");
                    if (sample == null) continue;

                    try
                    {
                        IMFMediaBuffer buffer;
                        if (sample.ConvertToContiguousBuffer(out buffer) == 0 && buffer != null)
                        {
                            try
                            {
                                IntPtr data; int max, len;
                                if (buffer.Lock(out data, out max, out len) == 0)
                                {
                                    try
                                    {
                                        int count = len / 2;
                                        short[] chunk = new short[count];
                                        Marshal.Copy(data, chunk, 0, count);
                                        interleaved.AddRange(chunk);
                                    }
                                    finally { buffer.Unlock(); }
                                }
                            }
                            finally { Marshal.ReleaseComObject(buffer); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(sample); }

                    if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                    {
                        IMFMediaType now;
                        if (reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, out now) == 0 && now != null)
                        {
                            int v;
                            if (now.GetUINT32(ref MfAudio.MF_MT_AUDIO_NUM_CHANNELS_, out v) == 0 && v > 0) channels = v;
                            if (now.GetUINT32(ref MfAudio.MF_MT_AUDIO_SAMPLES_PER_SECOND_, out v) == 0 && v > 0) rate = v;
                            Marshal.ReleaseComObject(now);
                        }
                    }
                }

                int frames = interleaved.Count / channels;
                if (frames == 0) throw new InvalidDataException("decoder produced no audio");

                float[][] chans = new float[channels][];
                for (int c = 0; c < channels; c++) chans[c] = new float[frames];
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < channels; c++)
                        chans[c][i] = interleaved[i * channels + c] / 32768f;

                WavData d = new WavData();
                d.SampleRate = rate;
                d.Channels = channels;
                d.Samples = chans;
                d.Frames = frames;
                d.FormatDescription = "Media Foundation, 16-bit PCM";
                return d;
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        /// <summary>Turns a Media Foundation failure into something a user can act on.</summary>
        public static string Describe(int hr, string path)
        {
            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            string hint;
            switch (ext)
            {
                case "ogg":
                case "oga":
                case "opus":
                    hint = "Windows has no built-in Ogg/Opus decoder. Install the free \"Web Media Extensions\" package " +
                           "from the Microsoft Store and this file will decode; otherwise convert it to WAV or FLAC first.";
                    break;
                case "ncw":
                    hint = "NCW is Steinberg's proprietary format and has no Windows decoder. Export the sample as WAV from " +
                           "the Steinberg software instead.";
                    break;
                case "m4a":
                case "aac":
                case "alac":
                    hint = "Windows could not decode this AAC/ALAC file. The HE-AAC and ALAC extensions come from the " +
                           "\"HEVC Video Extensions\" / media feature packs; try converting to WAV or FLAC.";
                    break;
                default:
                    hint = "Windows has no decoder for this file. Convert it to WAV, AIFF or FLAC first.";
                    break;
            }
            return "no decoder available (0x" + hr.ToString("X8") + "). " + hint;
        }

        // static copies of the GUIDs used as ref parameters (fields must be readable, not properties)
        internal static Guid MF_MT_AUDIO_NUM_CHANNELS_ = MF_MT_AUDIO_NUM_CHANNELS;
        internal static Guid MF_MT_AUDIO_SAMPLES_PER_SECOND_ = MF_MT_AUDIO_SAMPLES_PER_SECOND;
        internal static Guid MF_MT_MAJOR_TYPE_ = MF_MT_MAJOR_TYPE;
        internal static Guid MF_MT_SUBTYPE_ = MF_MT_SUBTYPE;
        internal static Guid MF_MT_AUDIO_BITS_PER_SAMPLE_ = MF_MT_AUDIO_BITS_PER_SAMPLE;
        internal static Guid MF_MT_AUDIO_BLOCK_ALIGNMENT_ = MF_MT_AUDIO_BLOCK_ALIGNMENT;
        internal static Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND_ = MF_MT_AUDIO_AVG_BYTES_PER_SECOND;
        internal static Guid MF_MT_ALL_SAMPLES_INDEPENDENT_ = MF_MT_ALL_SAMPLES_INDEPENDENT;
    }
}

