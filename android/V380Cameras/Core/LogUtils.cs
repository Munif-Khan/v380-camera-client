#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class LogUtils
    {
        public static bool enableDebug = false;

        // Independent from enableDebug: --rtsp-debug turns on only the
        // RTSP/RTP-path logs (client lifecycle, per-frame packetization),
        // without switching on the full --debug video pipeline dumps.
        public static bool enableRtspDebug = false;

        public static void debug(string log)
        {
            if (enableDebug)
                Console.Error.WriteLine(log);
        }

        public static void rtspDebug(string log)
        {
            if (enableRtspDebug)
                Console.Error.WriteLine(log);
        }
    }
}
