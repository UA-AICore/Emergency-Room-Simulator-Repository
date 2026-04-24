using System.Text;

namespace ERSimulatorApp.Services
{
    /// <summary>Wraps raw PCM s16le mono in a minimal WAV container for browser playback.</summary>
    public static class Pcm16MonoWavBuilder
    {
        public static byte[] Build(byte[] pcmS16leMono, int sampleRateHz)
        {
            if (pcmS16leMono.Length == 0)
                return Array.Empty<byte>();
            const ushort audioFormatPcm = 1;
            const ushort numChannels = 1;
            const ushort bitsPerSample = 16;
            var byteRate = sampleRateHz * numChannels * bitsPerSample / 8;
            var blockAlign = (ushort)(numChannels * bitsPerSample / 8);
            var dataSize = pcmS16leMono.Length;
            var riffSize = 36 + dataSize;

            using var ms = new MemoryStream(44 + dataSize);
            using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(riffSize);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write(audioFormatPcm);
            w.Write(numChannels);
            w.Write(sampleRateHz);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(bitsPerSample);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize);
            w.Write(pcmS16leMono);

            return ms.ToArray();
        }
    }
}
