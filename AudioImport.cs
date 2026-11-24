using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

internal static class AudioImport
{
    internal static Project Convert(string path, int bpm, out bool truncated)
    {
        double stepSeconds = 15.0 / bpm;
        int maxSteps = Studio.MaxBars * Synth.StepsPerBar;
        double rate;
        var samples = IsOgg(path)
            ? OggAudio.ReadMono(path, maxSteps * stepSeconds, out rate, out truncated)
            : WindowsMp3.ReadMono(path, maxSteps * stepSeconds, out rate, out truncated);
        int steps = Math.Min(maxSteps, (int)Math.Round(samples.Count / rate / stepSeconds, MidpointRounding.AwayFromZero));
        var notes = new bool[3, 8, maxSteps];
        var tracker = new PitchTracker(samples, rate);
        int detected = 0;
        for (int step = 0; step < steps; step++)
        {
            int row = tracker.FindRow(step * stepSeconds, stepSeconds);
            if (row < 0) continue;
            notes[0, row, step] = true;
            detected++;
        }

        if (detected == 0)
            throw new InvalidDataException("没有识别到清晰的旋律。请试试人声或单乐器更突出的音频。");
        int bars = (steps + Synth.StepsPerBar - 1) / Synth.StepsPerBar;
        return new Project(notes, bpm, bars);
    }

    private static bool IsOgg(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".ogg" || extension == ".oga" || extension == ".opus";
    }

    private sealed class PitchTracker
    {
        private readonly List<float> samples;
        private readonly double rate;
        private readonly int window;
        private readonly int minLag;
        private readonly int maxLag;
        private readonly double[] difference;
        private readonly double silenceThreshold;

        internal PitchTracker(List<float> samples, double rate)
        {
            this.samples = samples;
            this.rate = rate;
            window = (int)(rate * .040);
            minLag = Math.Max(2, (int)(rate / 1100));
            maxLag = (int)(rate / 120);
            difference = new double[maxLag + 1];
            float peak = 0;
            foreach (float sample in samples)
                peak = Math.Max(peak, Math.Abs(sample));
            silenceThreshold = Math.Max(.003, peak * .035);
        }

        internal int FindRow(double time, double duration)
        {
            int first = EstimateRow(time + duration * .30);
            int middle = EstimateRow(time + duration * .50);
            int last = EstimateRow(time + duration * .70);
            if (first >= 0 && first == last) return first;
            if (middle >= 0) return middle;
            return -1;
        }

        private int EstimateRow(double time)
        {
            int center = (int)(time * rate);
            if (center >= samples.Count || samples.Count < window + maxLag) return -1;
            int start = Math.Max(0, Math.Min(center - (window + maxLag) / 2,
                samples.Count - window - maxLag));
            double sum = 0;
            double energy = 0;
            for (int i = start; i < start + window; i++)
            {
                sum += samples[i];
                energy += samples[i] * samples[i];
            }
            double mean = sum / window;
            if (energy / window - mean * mean < silenceThreshold * silenceThreshold) return -1;

            // The first low minimum of the normalized difference estimates the fundamental period.
            double running = 0;
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double squared = 0;
                for (int i = start; i < start + window; i++)
                {
                    double delta = samples[i] - samples[i + lag];
                    squared += delta * delta;
                }
                running += squared;
                difference[lag] = running > 0 ? squared * lag / running : 1;
            }

            int period = minLag;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                if (difference[lag] < difference[period]) period = lag;
                if (difference[lag] >= .20) continue;
                period = lag;
                while (period < maxLag && difference[period + 1] < difference[period]) period++;
                break;
            }
            if (difference[period] > .30) return -1;

            double adjusted = period;
            if (period > minLag && period < maxLag)
            {
                double left = difference[period - 1];
                double middle = difference[period];
                double right = difference[period + 1];
                double curvature = left - 2 * middle + right;
                if (curvature > 0)
                    adjusted += Math.Max(-.5, Math.Min(.5, .5 * (left - right) / curvature));
            }

            double pitch = 69 + 12 * Math.Log(rate / adjusted / 440, 2);
            while (pitch < 59.5) pitch += 12;
            while (pitch > 72.5) pitch -= 12;
            int row = 0;
            for (int i = 1; i < 8; i++)
                if (Math.Abs(Synth.MelodyPitch(i) - pitch) < Math.Abs(Synth.MelodyPitch(row) - pitch))
                    row = i;
            return row;
        }
    }
}

internal static class WindowsMp3
{
    private const int AllStreams = -2;
    private const int FirstAudioStream = -3;
    private const uint EndOfStream = 2;
    private const uint ReaderError = 1;
    private const uint CurrentTypeChanged = 0x20;
    private const int MediaFoundationVersion = 0x00020070;

    private static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SampleRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Channels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Bits = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    internal static List<float> ReadMono(string path, double limitSeconds, out double rate, out bool truncated)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
        try
        {
            Marshal.ThrowExceptionForHR(MFStartup(MediaFoundationVersion, 0));
            try
            {
                ISourceReader reader;
                Marshal.ThrowExceptionForHR(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader));
                try { return ReadSamples(reader, limitSeconds, out rate, out truncated); }
                finally { Marshal.ReleaseComObject(reader); }
            }
            finally { MFShutdown(); }
        }
        finally { CoUninitialize(); }
    }

    private static List<float> ReadSamples(ISourceReader reader, double limitSeconds, out double rate, out bool truncated)
    {
        reader.SetStreamSelection(AllStreams, false);
        reader.SetStreamSelection(FirstAudioStream, true);
        IMediaType output;
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out output));
        try
        {
            SetGuid(output, MajorType, Audio);
            SetGuid(output, Subtype, Pcm);
            reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, output);
        }
        finally { Marshal.ReleaseComObject(output); }

        int nativeRate;
        int channels;
        GetFormat(reader, out nativeRate, out channels);
        int stride = Math.Max(1, (nativeRate + 4000) / 8000);
        rate = nativeRate / (double)stride;
        int limit = (int)Math.Ceiling(limitSeconds * rate);
        var samples = new List<float>(Math.Min(limit + 1, (int)rate * 8));
        var bytes = new byte[0];
        int accumulated = 0;
        double sum = 0;

        while (samples.Count <= limit)
        {
            uint flags;
            ISample sample;
            reader.ReadSample(FirstAudioStream, 0, IntPtr.Zero, out flags, IntPtr.Zero, out sample);
            try
            {
                if ((flags & ReaderError) != 0)
                    throw new InvalidDataException("MP3 解码失败。");
                if ((flags & CurrentTypeChanged) != 0)
                {
                    int updatedRate;
                    int updatedChannels;
                    GetFormat(reader, out updatedRate, out updatedChannels);
                    if (updatedRate != nativeRate || updatedChannels != channels)
                        throw new InvalidDataException("暂不支持中途切换采样率或声道数的 MP3。");
                }
                if (sample != null)
                {
                    int length = CopySample(sample, ref bytes);
                    int frameBytes = channels * 2;
                    for (int i = 0; i + frameBytes <= length && samples.Count <= limit; i += frameBytes)
                    {
                        for (int channel = 0; channel < frameBytes; channel += 2)
                            sum += (short)(bytes[i + channel] | (bytes[i + channel + 1] << 8));
                        if (++accumulated != stride) continue;
                        samples.Add((float)(sum / (32768.0 * channels * stride)));
                        accumulated = 0;
                        sum = 0;
                    }
                }
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
            }
            if ((flags & EndOfStream) != 0) break;
        }

        truncated = samples.Count > limit || (samples.Count == limit && accumulated > 0);
        if (samples.Count > limit) samples.RemoveAt(limit);
        else if (!truncated && accumulated > 0)
            samples.Add((float)(sum / (32768.0 * channels * accumulated)));
        return samples;
    }

    private static void GetFormat(ISourceReader reader, out int rate, out int channels)
    {
        IMediaType current;
        reader.GetCurrentMediaType(FirstAudioStream, out current);
        try
        {
            rate = GetValue(current, SampleRate);
            channels = GetValue(current, Channels);
            if (GetValue(current, Bits) != 16 || channels < 1 || channels > 8 || rate < 8000 || rate > 192000)
                throw new InvalidDataException("不支持此 MP3 的音频格式。");
        }
        finally { Marshal.ReleaseComObject(current); }
    }

    private static int CopySample(ISample sample, ref byte[] bytes)
    {
        IMediaBuffer buffer;
        sample.ConvertToContiguousBuffer(out buffer);
        try
        {
            IntPtr data;
            int length;
            buffer.Lock(out data, IntPtr.Zero, out length);
            try
            {
                if (bytes.Length < length) bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                return length;
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    private static int GetValue(IMediaType type, Guid key)
    {
        int value;
        type.GetUInt32(ref key, out value);
        return value;
    }

    private static void SetGuid(IMediaType type, Guid key, Guid value)
    {
        type.SetGuid(ref key, ref value);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint options);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMediaType type);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out ISourceReader reader);

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISourceReader
    {
        void _VtblGap1_1();
        void SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void _VtblGap2_1();
        void GetCurrentMediaType(int stream, out IMediaType type);
        void SetCurrentMediaType(int stream, IntPtr reserved, IMediaType type);
        void _VtblGap3_1();
        void ReadSample(int stream, uint controls, IntPtr actualStream, out uint flags, IntPtr time, out ISample sample);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaType
    {
        void _VtblGap1_4();
        void GetUInt32(ref Guid key, out int value);
        void _VtblGap2_16();
        void SetGuid(ref Guid key, ref Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISample
    {
        // IMFAttributes contributes 30 entries before the eight preceding IMFSample methods.
        void _VtblGap1_38();
        void ConvertToContiguousBuffer(out IMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaBuffer
    {
        void Lock(out IntPtr bytes, IntPtr maximumLength, out int currentLength);
        void Unlock();
    }
}
