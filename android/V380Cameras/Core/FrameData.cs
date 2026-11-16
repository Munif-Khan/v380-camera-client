#nullable disable

namespace V380Decoder.src
{
    public enum VideoCodec
    {
        Unknown,
        H264,
        H265
    }

    public class FrameData
    {
        public byte RawType;   // fragment header type byte (0x00/0x01/0x1A)
        public uint FrameId;
        public ushort FrameType;
        public ushort FrameRate;
        public ulong Timestamp;
        public VideoCodec Codec;
        public byte[] Payload;

        // Set explicitly by V380Client.HandleAsMediaFrame from IsKeyVideoFrame,
        // which already inspects NAL types (IDR/SPS/PPS/VPS) for both H264 and
        // H265. Kept as a plain field (rather than a second, independently
        // computed property) so RTSP keyframe-gating and video consumers all
        // agree on the exact same determination instead of re-deriving it.
        public bool IsKeyFrame;
    }
}

