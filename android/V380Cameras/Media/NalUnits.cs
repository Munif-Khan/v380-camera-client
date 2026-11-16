namespace V380Cameras.Media;

/// <summary>
/// Annex-B helpers shared by the live decoder and the SD-clip exporter.
/// The camera emits start-code prefixed NAL units for both H.264 and H.265.
/// </summary>
public static class NalUnits
{
    /// <summary>
    /// Pulls SPS/PPS (and VPS for HEVC) out of an access unit. The returned
    /// buffers keep their 4-byte start codes because MediaFormat's csd-0/csd-1
    /// and MediaMuxer both want them that way.
    /// </summary>
    public static bool TrySplitParameterSets(
        byte[] accessUnit, bool hevc,
        out byte[]? sps, out byte[]? pps, out byte[]? vps)
    {
        sps = pps = vps = null;
        int i = 0;
        while (i + 4 < accessUnit.Length)
        {
            if (accessUnit[i] != 0 || accessUnit[i + 1] != 0 || accessUnit[i + 2] != 1)
            {
                i++;
                continue;
            }

            int header = i + 3;
            if (header >= accessUnit.Length) break;
            int type = hevc ? (accessUnit[header] >> 1) & 0x3F : accessUnit[header] & 0x1F;

            int next = header + 1;
            while (next + 3 <= accessUnit.Length)
            {
                if (accessUnit[next] == 0 && accessUnit[next + 1] == 0 &&
                    (accessUnit[next + 2] == 1 ||
                     (accessUnit[next + 2] == 0 && next + 3 < accessUnit.Length && accessUnit[next + 3] == 1)))
                    break;
                next++;
            }

            var nal = new byte[next - i];
            Array.Copy(accessUnit, i, nal, 0, nal.Length);
            nal = NormaliseStartCode(nal);

            if (hevc)
            {
                if (type == 32) vps ??= nal;      // VPS
                else if (type == 33) sps ??= nal; // SPS
                else if (type == 34) pps ??= nal; // PPS
            }
            else
            {
                if (type == 7) sps ??= nal;
                else if (type == 8) pps ??= nal;
            }

            i = next;
        }

        if (hevc) return sps != null && pps != null && vps != null;
        return sps != null && pps != null;
    }

    /// <summary>Promotes a 3-byte start code to 4 bytes, as MediaFormat expects.</summary>
    public static byte[] NormaliseStartCode(byte[] nal)
    {
        if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1)
        {
            var padded = new byte[nal.Length + 1];
            padded[0] = 0;
            Array.Copy(nal, 0, padded, 1, nal.Length);
            return padded;
        }
        return nal;
    }

    /// <summary>
    /// Converts an Annex-B access unit (start-code delimited) to AVCC format
    /// (4-byte big-endian length prefix per NAL unit), which MediaMuxer
    /// WriteSampleData requires for MP4 tracks.
    /// </summary>
    public static byte[] AnnexBToAvcc(byte[] annexB)
    {
        // Collect NAL unit byte ranges (excluding their start codes).
        var nals = new List<(int offset, int length)>();
        int i = 0;
        int start = -1;
        while (i < annexB.Length)
        {
            bool is3 = i + 2 < annexB.Length &&
                        annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1;
            bool is4 = i + 3 < annexB.Length &&
                        annexB[i] == 0 && annexB[i + 1] == 0 &&
                        annexB[i + 2] == 0 && annexB[i + 3] == 1;
            if (is3 || is4)
            {
                if (start >= 0)
                    nals.Add((start, i - start));
                start = i + (is4 ? 4 : 3);
                i += is4 ? 4 : 3;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < annexB.Length)
            nals.Add((start, annexB.Length - start));

        if (nals.Count == 0)
        {
            // No start codes found — wrap the whole buffer as one NAL unit.
            var fallback = new byte[4 + annexB.Length];
            fallback[0] = (byte)(annexB.Length >> 24);
            fallback[1] = (byte)(annexB.Length >> 16);
            fallback[2] = (byte)(annexB.Length >> 8);
            fallback[3] = (byte)annexB.Length;
            Array.Copy(annexB, 0, fallback, 4, annexB.Length);
            return fallback;
        }

        int total = nals.Sum(n => 4 + n.length);
        var avcc = new byte[total];
        int dst = 0;
        foreach (var (offset, length) in nals)
        {
            avcc[dst]     = (byte)(length >> 24);
            avcc[dst + 1] = (byte)(length >> 16);
            avcc[dst + 2] = (byte)(length >> 8);
            avcc[dst + 3] = (byte)length;
            Array.Copy(annexB, offset, avcc, dst + 4, length);
            dst += 4 + length;
        }
        return avcc;
    }
}
