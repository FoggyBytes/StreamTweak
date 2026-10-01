using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamTweak
{
    /// <summary>
    /// The one place that turns a streaming server's encoder name into what StreamTweak shows,
    /// and says whether that encoder runs on the GPU's video-encode engine (9.1.0, §82).
    /// <para>The names are the ones the server writes in <c>Creating encoder [name]</c>:
    /// FFmpeg's codec names for Sunshine, Apollo, Vibeshine and Vibepollo
    /// (<c>{h264,hevc,av1}_{nvenc,amf,qsv,vaapi,videotoolbox,vulkan,mf}</c>, the software
    /// <c>libx264</c>, <c>libx265</c>, <c>libsvtav1</c>), plus <c>pyrowave</c>, which Vibeshine and
    /// Vibepollo 2.0 write for their own encoder. Censused in each server's <c>src/video.cpp</c>
    /// on 30/09/2026. An unknown name is shown as the server wrote it, never guessed at.</para>
    /// </summary>
    public static class EncoderNames
    {
        /// <summary>"AV1 · NVENC", "PyroWave", "HEVC · software"; the raw name when unknown.</summary>
        public static string Label(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "—";
            string n = name.Trim().ToLowerInvariant();

            if (n == "pyrowave") return "PyroWave";
            if (n == "libx264")   return "H.264 · software";
            if (n == "libx265")   return "HEVC · software";
            if (n == "libsvtav1") return "AV1 · software";

            int us = n.IndexOf('_');
            if (us <= 0 || us == n.Length - 1) return name.Trim();
            string? codec = n[..us] switch
            {
                "h264" => "H.264",
                "hevc" => "HEVC",
                "av1"  => "AV1",
                _      => null,
            };
            string? backend = n[(us + 1)..] switch
            {
                "nvenc"        => "NVENC",
                "amf"          => "AMF",
                "qsv"          => "Quick Sync",
                "vaapi"        => "VA-API",
                "videotoolbox" => "VideoToolbox",
                "vulkan"       => "Vulkan",
                "mf"           => "Media Foundation",
                _              => null,
            };
            return codec != null && backend != null ? $"{codec} · {backend}" : name.Trim();
        }

        /// <summary>
        /// False for the encoders whose work the "Enc" figure cannot see, because it reads the
        /// GPU's video-encode engine (PDH <c>engtype_VideoEncode</c>): PyroWave encodes with
        /// Vulkan compute on the shaders, so its load is inside the GPU figure; the software
        /// encoders run on the CPU. True for the hardware encoders and for an unknown name —
        /// "we do not know" must not relabel a figure as not applicable.
        /// </summary>
        public static bool UsesVideoEncodeEngine(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string n = name.Trim().ToLowerInvariant();
            return n != "pyrowave" && !n.StartsWith("lib", StringComparison.Ordinal);
        }

        /// <summary>
        /// Where the work of an encoder that bypasses the video-encode engine shows up instead:
        /// "GPU" for PyroWave (shaders), "CPU" for software. Null for the others.
        /// </summary>
        public static string? LoadShownIn(string? name)
        {
            if (UsesVideoEncodeEngine(name)) return null;
            return name!.Trim().Equals("pyrowave", StringComparison.OrdinalIgnoreCase) ? "GPU" : "CPU";
        }
    }
    /// <summary>
    /// What a session's streams were encoded with, summed up once for every view that needs it
    /// (detail, Compare, timeline) — 9.1.0, §82. Built from <see cref="StreamSpan.Encoder"/>;
    /// a record written before 9.1.0 has none and reads as <see cref="Known"/> = false.
    /// </summary>
    public sealed class EncoderSummary
    {
        /// <summary>At least one stream named its encoder.</summary>
        public bool Known { get; private init; }

        /// <summary>Every stream with a known encoder bypassed the video-encode engine (PyroWave, software).</summary>
        public bool AllBypass { get; private init; }

        /// <summary>Some streams used the video-encode engine and some did not.</summary>
        public bool Mixed { get; private init; }

        /// <summary>
        /// True when the saved "Enc" figure means what it always meant — the video-encode engine
        /// at work — so it can be shown as a percentage and compared: every known encoder uses the
        /// engine, or none is known (records before 9.1.0, all of them from before PyroWave).
        /// </summary>
        public bool EncFigureComparable => !AllBypass && !Mixed;

        /// <summary>The label that streamed longest ("PyroWave"), or null when none is known.</summary>
        public string? Primary { get; private init; }

        /// <summary>The other labels, longest first.</summary>
        public IReadOnlyList<string> Others { get; private init; } = Array.Empty<string>();

        /// <summary>Streams that used <see cref="Primary"/>, and all the session's streams.</summary>
        public int PrimaryStreams { get; private init; }
        public int TotalStreams   { get; private init; }

        /// <summary>Label of a bypassing encoder ("PyroWave"), for "n/a — PyroWave"; null if none.</summary>
        public string? BypassLabel { get; private init; }

        /// <summary>Where that encoder's load shows up instead: "GPU" or "CPU".</summary>
        public string? BypassLoadIn { get; private init; }

        public static EncoderSummary Of(IReadOnlyList<StreamSpan>? spans, DateTime? sessionEnd)
        {
            if (spans == null || spans.Count == 0) return new EncoderSummary();
            var known = spans.Where(s => !string.IsNullOrWhiteSpace(s.Encoder)).ToList();
            if (known.Count == 0) return new EncoderSummary { TotalStreams = spans.Count };

            double Dur(StreamSpan s) => Math.Max(0, ((s.End ?? sessionEnd ?? s.Start) - s.Start).TotalSeconds);
            var byLabel = known
                .GroupBy(s => EncoderNames.Label(s.Encoder))
                .Select(g => (Label: g.Key, Seconds: g.Sum(Dur), Count: g.Count()))
                .OrderByDescending(g => g.Seconds)
                .ToList();
            bool anyEngine = known.Any(s => EncoderNames.UsesVideoEncodeEngine(s.Encoder));
            var bypass     = known.FirstOrDefault(s => !EncoderNames.UsesVideoEncodeEngine(s.Encoder));

            return new EncoderSummary
            {
                Known          = true,
                AllBypass      = !anyEngine,
                Mixed          = anyEngine && bypass != null,
                Primary        = byLabel[0].Label,
                Others         = byLabel.Skip(1).Select(g => g.Label).ToList(),
                PrimaryStreams = byLabel[0].Count,
                TotalStreams   = spans.Count,
                BypassLabel    = bypass != null ? EncoderNames.Label(bypass.Encoder) : null,
                BypassLoadIn   = bypass != null ? EncoderNames.LoadShownIn(bypass.Encoder) : null,
            };
        }
    }
}
