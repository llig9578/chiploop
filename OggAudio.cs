using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Concentus.Oggfile;
using Concentus;
using NVorbis;

internal static class OggAudio
{
    static OggAudio()
    {
        AppDomain.CurrentDomain.AssemblyResolve += LoadCodec;
    }

    internal static List<float> ReadMono(string path, double limit, out double rate, out bool truncated)
    {
        using (var file = File.OpenRead(path))
        {
            byte[] packet = ReadIdentification(file);
            if (StartsWith(packet, 0, "OpusHead"))
            {
                int channels = packet[9];
                if ((channels != 1 && channels != 2) || packet[18] != 0)
                    throw new InvalidDataException("目前支持单声道和双声道的 Ogg Opus 音频。");
                int preskip = packet[10] | (packet[11] << 8);
                return ReadOpus(file, channels, preskip, limit, out rate, out truncated);
            }
            if (packet[0] == 1 && StartsWith(packet, 1, "vorbis"))
                return ReadVorbis(file, limit, out rate, out truncated);

            throw new InvalidDataException("此 OGG 文件使用了不支持的编码。请选择 Vorbis 或 Opus 音频。");
        }
    }

    private static byte[] ReadIdentification(FileStream file)
    {
        using (var reader = new BinaryReader(file, System.Text.Encoding.UTF8, true))
        {
            byte[] page = reader.ReadBytes(27);
            if (page.Length != 27 || !StartsWith(page, 0, "OggS") || page[4] != 0 ||
                (page[5] & 2) == 0 || page[26] == 0)
                throw new InvalidDataException("无法读取 OGG 文件头，请确认文件完整。");

            byte[] sizes = reader.ReadBytes(page[26]);
            if (sizes.Length != page[26] || sizes[0] < 19)
                throw new InvalidDataException("无法读取 OGG 音频编码。");
            byte[] identification = reader.ReadBytes(19);
            if (identification.Length != 19)
                throw new InvalidDataException("OGG 文件不完整。");
            file.Position = 0;
            return identification;
        }
    }

    private static bool StartsWith(byte[] data, int offset, string signature)
    {
        for (int i = 0; i < signature.Length; i++)
            if (data[offset + i] != signature[i]) return false;
        return true;
    }

    private static List<float> ReadVorbis(Stream file, double limit, out double rate, out bool truncated)
    {
        using (var reader = new VorbisReader(file, false))
        {
            int channels = reader.Channels;
            if (channels < 1 || channels > 8 || reader.SampleRate < 8000 || reader.SampleRate > 192000)
                throw new InvalidDataException("不支持此 Ogg Vorbis 的采样率或声道数。");

            var mono = new OggMono(reader.SampleRate, limit);
            var buffer = new float[1024 * channels];
            int length;
            while (!mono.Full && (length = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i + channels <= length && !mono.Full; i += channels)
                {
                    double sum = 0;
                    for (int channel = 0; channel < channels; channel++)
                        sum += buffer[i + channel];
                    mono.Add(sum / channels);
                }
            }
            rate = mono.Rate;
            return mono.Finish(out truncated);
        }
    }

    private static List<float> ReadOpus(Stream file, int channels, int preskip, double limit, out double rate, out bool truncated)
    {
        const int nativeRate = 48000;
        var mono = new OggMono(nativeRate, limit);
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        using (var decoder = OpusCodecFactory.CreateDecoder(nativeRate, channels))
        {
            var reader = new OpusOggReadStream(decoder, file);
            long remaining = reader.GranuleCount > preskip ? reader.GranuleCount - preskip : long.MaxValue;
            while (reader.HasNextPacket && !mono.Full && remaining > 0)
            {
                short[] packet = reader.DecodeNextPacket();
                if (packet == null) break;
                for (int i = 0; i + channels <= packet.Length && !mono.Full && remaining > 0; i += channels)
                {
                    if (preskip > 0)
                    {
                        preskip--;
                        continue;
                    }

                    int sum = 0;
                    for (int channel = 0; channel < channels; channel++)
                        sum += packet[i + channel];
                    mono.Add(sum / (32768.0 * channels));
                    remaining--;
                }
            }

            if (!string.IsNullOrEmpty(reader.LastError))
                throw new InvalidDataException("OGG Opus 数据损坏或无法读取。");
        }
        rate = mono.Rate;
        return mono.Finish(out truncated);
    }

    private static Assembly LoadCodec(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        using (var resource = typeof(OggAudio).Assembly.GetManifestResourceStream("Codec." + name + ".dll"))
        {
            if (resource == null) return null;
            using (var assembly = new MemoryStream())
            {
                resource.CopyTo(assembly);
                return Assembly.Load(assembly.ToArray());
            }
        }
    }

    private sealed class OggMono
    {
        private readonly int stride;
        private readonly int limit;
        private readonly List<float> samples;
        private double sum;
        private int count;

        internal readonly double Rate;
        internal bool Full { get { return samples.Count > limit; } }

        internal OggMono(int nativeRate, double seconds)
        {
            stride = Math.Max(1, (nativeRate + 4000) / 8000);
            Rate = nativeRate / (double)stride;
            limit = (int)Math.Ceiling(seconds * Rate);
            samples = new List<float>(Math.Min(limit + 1, (int)Rate * 8));
        }

        internal void Add(double frame)
        {
            sum += frame;
            if (++count != stride) return;
            samples.Add((float)(sum / stride));
            sum = 0;
            count = 0;
        }

        internal List<float> Finish(out bool truncated)
        {
            truncated = Full || (samples.Count == limit && count > 0);
            if (Full) samples.RemoveAt(limit);
            else if (!truncated && count > 0) samples.Add((float)(sum / count));
            return samples;
        }
    }
}
