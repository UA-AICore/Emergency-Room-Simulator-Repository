using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using NLayer;

namespace ERSimulatorApp.Services
{
    /// <summary>
    /// Unofficial Microsoft Edge online TTS (same service as the edge-tts tool). No API key or ElevenLabs TTS.
    /// Dev/testing only — unofficial, may break. Decodes MP3 with embedded <c>NLayer</c>; if that fails, uses <c>ffmpeg</c> on PATH when available.
    /// Set <c>ElevenLabs:TtsEngine</c> to <c>MicrosoftEdgeFree</c>.
    /// Tune delivery with <c>ElevenLabs:EdgeTtsVoice</c> (e.g. male <c>en-US-GuyNeural</c> for instructor avatars),
    /// <c>EdgeTtsProsodyPitch</c> (e.g. <c>+18%</c>, <c>+40Hz</c>, <c>+1st</c>), and <c>EdgeTtsProsodyRate</c> (e.g. <c>-60%</c> slower, <c>+5%</c> faster).
    /// </summary>
    public sealed class MicrosoftEdgeFreePcmTtsService : IElevenLabsTextToSpeechService
    {
        private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const long WinEpochSeconds = 11_644_473_600L;
        private const string DefaultVoice = "en-US-EmmaMultilingualNeural";
        private const string DefaultChromeMajor = "143";
        private const string DefaultSecMsGecVersion = "1-143.0.3650.75";

        private readonly ILogger<MicrosoftEdgeFreePcmTtsService> _logger;
        private readonly IConfiguration _configuration;

        public MicrosoftEdgeFreePcmTtsService(ILogger<MicrosoftEdgeFreePcmTtsService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<ElevenLabsPcmTtsResult> SynthesizePcm24kAsync(string text, CancellationToken cancellationToken = default)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "Empty text after trim.");

            var voice = (_configuration["ElevenLabs:EdgeTtsVoice"] ?? DefaultVoice).Trim();
            var major = (_configuration["ElevenLabs:EdgeTtsChromeMajor"] ?? DefaultChromeMajor).Trim();
            var secVer = (_configuration["ElevenLabs:EdgeTtsSecMsGecVersion"] ?? DefaultSecMsGecVersion).Trim();
            var prosodyPitch = SanitizeSsmlProsodyToken(_configuration["ElevenLabs:EdgeTtsProsodyPitch"], "+0Hz");
            var prosodyRate = SanitizeSsmlProsodyToken(_configuration["ElevenLabs:EdgeTtsProsodyRate"], "+0%");
            _logger.LogDebug("Edge TTS prosody: voice={Voice} pitch={Pitch} rate={Rate}", voice, prosodyPitch, prosodyRate);

            using var mp3Stream = new MemoryStream();
            foreach (var chunk in SplitUtf8Chunks(SanitizeForEdge(trimmed), 4096))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = await SynthesizeChunkToMp3Async(chunk, voice, major, secVer, prosodyPitch, prosodyRate, cancellationToken);
                if (part.Length == 0)
                    return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "Edge TTS returned no audio (service may have changed or blocked the request).");
                await mp3Stream.WriteAsync(part, cancellationToken);
            }

            var mp3Bytes = mp3Stream.ToArray();
            var pcm = TryNLayerMp3ToPcm24kMono(mp3Bytes, _logger);
            var ffmpegPath = FindFfmpegExecutable();
            if (pcm.Length == 0 && !string.IsNullOrEmpty(ffmpegPath))
                pcm = await FfmpegMp3ToPcm24kMonoAsync(ffmpegPath, mp3Bytes, _logger, cancellationToken);

            if (pcm.Length == 0)
            {
                var hint = string.IsNullOrEmpty(ffmpegPath)
                    ? "NLayer decode failed (no ffmpeg on PATH for fallback)."
                    : "NLayer and ffmpeg decode both failed.";
                return new ElevenLabsPcmTtsResult(Array.Empty<byte>(), "Could not decode Edge TTS MP3 to PCM 24 kHz mono: " + hint);
            }

            _logger.LogInformation("MicrosoftEdgeFree TTS: {Bytes} bytes PCM 24kHz", pcm.Length);
            return new ElevenLabsPcmTtsResult(pcm, null);
        }

        private static string SanitizeForEdge(string s)
        {
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = (int)chars[i];
                if (c is >= 0 and <= 8 or 11 or 12 or (>= 14 and <= 31))
                    chars[i] = ' ';
            }
            return new string(chars);
        }

        private static IEnumerable<string> SplitUtf8Chunks(string text, int maxBytes)
        {
            var enc = Encoding.UTF8;
            var full = enc.GetBytes(text);
            if (full.Length <= maxBytes)
            {
                yield return text;
                yield break;
            }
            var start = 0;
            while (start < full.Length)
            {
                var len = Math.Min(maxBytes, full.Length - start);
                while (len > 0 && (full[start + len - 1] & 0xC0) == 0x80)
                    len--;
                if (len == 0) len = 1;
                yield return enc.GetString(full.AsSpan(start, len));
                start += len;
            }
        }

        private static string EscapeSsml(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>SSML prosody pitch/rate for Edge (e.g. <c>+6%</c>, <c>+40Hz</c>, <c>+1st</c>, <c>x-low</c>, <c>-12%</c> rate). Rejects quotes/XML chars.</summary>
        private static string SanitizeSsmlProsodyToken(string? raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            var t = raw.Trim();
            if (t.Length > 28) return fallback;
            foreach (var c in t)
            {
                if (c is '\'' or '"' or '<' or '>' or '&') return fallback;
                if (char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.' or '%' or '_') continue;
                return fallback;
            }

            return t;
        }

        private static string BuildSsml(string voice, string escapedText, string pitchAttr, string rateAttr) =>
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            "<voice name='" + voice + "'>" +
            "<prosody pitch='" + pitchAttr + "' rate='" + rateAttr + "' volume='+0%'>" + escapedText + "</prosody></voice></speak>";

        private static string EdgeTimestamp() =>
            DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT+0000 (Coordinated Universal Time)";

        private static string GenerateSecMsGec()
        {
            var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var t = (double)unix + WinEpochSeconds;
            t -= t % 300;
            var ticks = (ulong)Math.Floor(t * 10_000_000.0);
            var str = ticks.ToString(CultureInfo.InvariantCulture) + TrustedClientToken;
            return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(str)));
        }

        private async Task<byte[]> SynthesizeChunkToMp3Async(string text, string voice, string chromeMajor, string secMsGecVersion, string prosodyPitch, string prosodyRate, CancellationToken ct)
        {
            var connectId = Guid.NewGuid().ToString("N");
            var gec = GenerateSecMsGec();
            var uri = new Uri(
                "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
                $"?TrustedClientToken={TrustedClientToken}&ConnectionId={connectId}&Sec-MS-GEC={gec}&Sec-MS-GEC-Version={Uri.EscapeDataString(secMsGecVersion)}");

            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Pragma", "no-cache");
            ws.Options.SetRequestHeader("Cache-Control", "no-cache");
            ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
            ws.Options.SetRequestHeader("User-Agent",
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.0.0 Safari/537.36 Edg/{chromeMajor}.0.0.0");
            ws.Options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
            ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
            ws.Options.SetRequestHeader("Cookie", "muid=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToUpperInvariant() + ";");

            await ws.ConnectAsync(uri, ct);

            var speechConfig =
                $"X-Timestamp:{EdgeTimestamp()}\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
                "\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"" +
                "},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
            await ws.SendAsync(Encoding.UTF8.GetBytes(speechConfig), WebSocketMessageType.Text, true, ct);

            var requestId = Guid.NewGuid().ToString("N");
            var ts = EdgeTimestamp();
            var ssmlPayload =
                $"X-RequestId:{requestId}\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                $"X-Timestamp:{ts}Z\r\n" +
                "Path:ssml\r\n\r\n" +
                BuildSsml(voice, EscapeSsml(text), prosodyPitch, prosodyRate);
            await ws.SendAsync(Encoding.UTF8.GetBytes(ssmlPayload), WebSocketMessageType.Text, true, ct);

            using var audio = new MemoryStream();
            var buffer = new byte[65536];
            var gotAudio = false;
            while (ws.State == WebSocketState.Open)
            {
                var seg = await ws.ReceiveAsync(buffer, ct);
                if (seg.MessageType == WebSocketMessageType.Close)
                    break;
                if (seg.MessageType == WebSocketMessageType.Text)
                {
                    if (Encoding.UTF8.GetString(buffer, 0, seg.Count).Contains("Path:turn.end", StringComparison.OrdinalIgnoreCase))
                        break;
                    continue;
                }
                if (seg.MessageType != WebSocketMessageType.Binary || seg.Count < 4)
                    continue;

                // Same layout as edge-tts: first uint16 BE = total bytes of header block including those 2 bytes; then header lines; then 2-byte gap; then MP3.
                var headerLen = (buffer[0] << 8) | buffer[1];
                if (headerLen < 2 || headerLen + 2 > seg.Count)
                    continue;

                var headerText = Encoding.UTF8.GetString(buffer, 2, headerLen - 2);
                if (!headerText.Contains("Path:audio", StringComparison.OrdinalIgnoreCase))
                    continue;

                var payloadOffset = headerLen + 2;
                var audioLen = seg.Count - payloadOffset;
                if (audioLen <= 0)
                    continue;

                await audio.WriteAsync(buffer.AsMemory(payloadOffset, audioLen), ct);
                gotAudio = true;
            }

            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* ignore */ }
            return gotAudio ? audio.ToArray() : Array.Empty<byte>();
        }

        /// <summary>Decode Edge MP3 (24 kHz mono typical) to s16le mono PCM at 24 kHz using NLayer.</summary>
        private static byte[] TryNLayerMp3ToPcm24kMono(byte[] mp3, ILogger logger)
        {
            if (mp3.Length == 0) return Array.Empty<byte>();
            try
            {
                using var ms = new MemoryStream(mp3, writable: false);
                using var mpeg = new MpegFile(ms);
                var rate = mpeg.SampleRate;
                var ch = mpeg.Channels;
                if (ch <= 0 || rate <= 0) return Array.Empty<byte>();

                const int bufFloats = 8192;
                var floatBuf = new float[bufFloats];
                using var outMs = new MemoryStream();
                while (true)
                {
                    var n = mpeg.ReadSamples(floatBuf, 0, floatBuf.Length);
                    if (n <= 0) break;

                    if (ch == 1)
                    {
                        for (var i = 0; i < n; i++)
                            WriteS16Le(outMs, FloatToS16(floatBuf[i]));
                    }
                    else
                    {
                        for (var i = 0; i + ch - 1 < n; i += ch)
                        {
                            double sum = 0;
                            for (var c = 0; c < ch; c++)
                                sum += floatBuf[i + c];
                            WriteS16Le(outMs, FloatToS16((float)(sum / ch)));
                        }
                    }
                }

                var raw = outMs.ToArray();
                if (raw.Length == 0) return raw;
                return rate == 24000 ? raw : PcmS16LeMonoResampler.Resample(raw, rate, 24000);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "NLayer MP3 decode failed");
                return Array.Empty<byte>();
            }
        }

        private static short FloatToS16(float f)
        {
            f = Math.Clamp(f, -1f, 1f);
            return (short)Math.Round(f * 32767f);
        }

        private static void WriteS16Le(MemoryStream ms, short s)
        {
            ms.WriteByte((byte)(s & 0xff));
            ms.WriteByte((byte)((s >> 8) & 0xff));
        }

        private static string? FindFfmpegExecutable()
        {
            var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch { /* ignore */ }
            }
            return null;
        }

        private static async Task<byte[]> FfmpegMp3ToPcm24kMonoAsync(string ffmpeg, byte[] mp3, ILogger logger, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            var tmpIn = Path.Combine(Path.GetTempPath(), "codira-edge-" + id + ".mp3");
            var tmpOut = Path.Combine(Path.GetTempPath(), "codira-edge-" + id + ".pcm");
            try
            {
                await File.WriteAllBytesAsync(tmpIn, mp3, ct);
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -hide_banner -loglevel error -i \"{tmpIn}\" -f s16le -acodec pcm_s16le -ar 24000 -ac 1 \"{tmpOut}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return Array.Empty<byte>();
                var err = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                {
                    logger.LogWarning("ffmpeg exit {Code}: {Err}", p.ExitCode, err);
                    return Array.Empty<byte>();
                }
                return await File.ReadAllBytesAsync(tmpOut, ct);
            }
            finally
            {
                try { File.Delete(tmpIn); } catch { }
                try { File.Delete(tmpOut); } catch { }
            }
        }
    }
}
