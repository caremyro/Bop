using System;
using System.Collections.Generic;
using System.Linq;
using YoutubeDLSharp.Options;
using YoutubeDLSharp.Metadata;

namespace Bop.Services
{
    public class YoutubeUrlResolver
    {
        /// <summary>
        /// Filtre et retourne l'URL directe du meilleur flux audio à partir des formats YoutubeDLSharp.
        /// </summary>
        public string? GetBestAudioUrl(IEnumerable<FormatData>? formats)
        {
            if (formats == null) return null;

            var bestFormat = formats
                .Where(f => !string.IsNullOrEmpty(f.Url) && 
                            (string.IsNullOrEmpty(f.VideoCodec) || 
                             "none".Equals(f.VideoCodec, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.AudioBitrate ?? f.Bitrate ?? 0)
                .ThenBy(f => GetCodecRank(f.AudioCodec))
                .FirstOrDefault();

            return bestFormat?.Url;
        }

        private int GetCodecRank(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return 99;

            string c = codec.ToLower();
            if (c.Contains("opus")) return 0;
            if (c.Contains("mp4a") || c.Contains("aac")) return 1;
            if (c.Contains("vorbis")) return 2;
            if (c.Contains("mp3")) return 3;

            return 90;
        }
    }
}