namespace V380Cameras.Media;

/// <summary>
/// G.711 A-law, the format the camera's live audio socket uses
/// (8000 Hz, mono, 8 bit). Android's AudioTrack cannot play A-law directly,
/// so it is expanded to 16-bit PCM for the WAV stream the UI plays.
/// </summary>
public static class AlawCodec
{
    public const int SampleRate = 8000;

    private static readonly short[] Table = BuildTable();

    private static short[] BuildTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int a = i ^ 0x55;
            int t = (a & 0x0F) << 4;
            int segment = (a & 0x70) >> 4;
            if (segment == 0) t += 8;
            else if (segment == 1) t += 0x108;
            else { t += 0x108; t <<= segment - 1; }
            table[i] = (short)((a & 0x80) != 0 ? t : -t);
        }
        return table;
    }

    /// <summary>Expands A-law bytes to little-endian 16-bit PCM.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> alaw)
    {
        var pcm = new byte[alaw.Length * 2];
        for (int i = 0; i < alaw.Length; i++)
        {
            short sample = Table[alaw[i]];
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    /// <summary>RIFF header for a live mono 16-bit PCM stream of unknown length.</summary>
    public static byte[] WavHeader()
    {
        var header = new byte[44];
        void Text(int at, string value) => System.Text.Encoding.ASCII.GetBytes(value).CopyTo(header, at);
        void Int32At(int at, uint value)
        {
            header[at] = (byte)value; header[at + 1] = (byte)(value >> 8);
            header[at + 2] = (byte)(value >> 16); header[at + 3] = (byte)(value >> 24);
        }
        void Int16At(int at, ushort value) { header[at] = (byte)value; header[at + 1] = (byte)(value >> 8); }

        Text(0, "RIFF");
        Int32At(4, 0xFFFFFFFF);
        Text(8, "WAVE");
        Text(12, "fmt ");
        Int32At(16, 16);
        Int16At(20, 1);                 // PCM
        Int16At(22, 1);                 // mono
        Int32At(24, SampleRate);
        Int32At(28, SampleRate * 2);    // byte rate
        Int16At(32, 2);                 // block align
        Int16At(34, 16);                // bits per sample
        Text(36, "data");
        Int32At(40, 0xFFFFFFFF);
        return header;
    }
}
