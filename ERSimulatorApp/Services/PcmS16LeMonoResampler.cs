namespace ERSimulatorApp.Services
{
    /// <summary>Linear resample of 16-bit LE mono PCM (e.g. TTS 24 kHz → LiveAvatar agent.speak at another rate).</summary>
    public static class PcmS16LeMonoResampler
    {
        public static byte[] Resample(byte[] pcm16leMono, int srcRateHz, int dstRateHz)
        {
            if (srcRateHz <= 0 || dstRateHz <= 0 || srcRateHz == dstRateHz)
                return pcm16leMono;
            var nSrc = pcm16leMono.Length / 2;
            if (nSrc <= 0)
                return pcm16leMono;

            var ratio = (double)dstRateHz / srcRateHz;
            var nDst = Math.Max(1, (int)Math.Round(nSrc * ratio));
            var dst = new byte[nDst * 2];
            for (var i = 0; i < nDst; i++)
            {
                var srcPos = i / ratio;
                var i0 = (int)srcPos;
                var frac = srcPos - i0;
                var s0 = ReadS16Le(pcm16leMono, i0);
                var s1 = i0 + 1 < nSrc ? ReadS16Le(pcm16leMono, i0 + 1) : s0;
                var v = s0 + (s1 - s0) * frac;
                WriteS16Le(dst, i, (short)Math.Clamp((int)Math.Round(v), short.MinValue, short.MaxValue));
            }

            return dst;
        }

        private static short ReadS16Le(byte[] buf, int sampleIndex)
        {
            var o = sampleIndex * 2;
            if (o + 1 >= buf.Length) return 0;
            return (short)(buf[o] | (buf[o + 1] << 8));
        }

        private static void WriteS16Le(byte[] buf, int sampleIndex, short s)
        {
            var o = sampleIndex * 2;
            if (o + 1 >= buf.Length) return;
            buf[o] = (byte)(s & 0xff);
            buf[o + 1] = (byte)((s >> 8) & 0xff);
        }
    }
}
