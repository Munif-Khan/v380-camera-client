#nullable disable

using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace V380Decoder.src
{
    public class V380Client : IDisposable
    {
        private TcpClient authClient, streamClient;
        private NetworkStream authStream = null!, streamStream = null!;
        private readonly string ip;
        private readonly int port;
        private readonly uint deviceId;
        private readonly string username, password;
        private readonly SourceStream source;
        private readonly OutputMode mode;
        private readonly int streamQuality;
        private uint authTicket, sessionId;
        private ushort deviceVersion, communicationVersion;
        private int frameWidth = 1280;
        private int frameheight = 720;
        private byte[] aesKey = new byte[16];
        private bool needReconnect = false;

        // --dump-frames support: bounded diagnostic dump, never fills the disk.
        private const int MaxDumpFrames = 5;
        private readonly bool dumpFramesEnabled;
        private readonly string dumpDir;
        private readonly object dumpLock = new();
        private int dumpFrameCounter = 0;

        public V380Client(string ip, int port, uint deviceId, string username, string password, SourceStream source, OutputMode mode, int streamQuality, bool dumpFrames = false)
        {
            this.ip = ip;
            this.port = port;
            this.deviceId = deviceId;
            this.username = username;
            this.password = password;
            this.source = source;
            this.mode = mode;
            this.streamQuality = streamQuality;

            dumpFramesEnabled = dumpFrames;
            if (dumpFramesEnabled)
            {
                dumpDir = Path.Combine(Directory.GetCurrentDirectory(), "frame_dumps");
                Directory.CreateDirectory(dumpDir);
                Console.Error.WriteLine($"[DUMP] frame dumping enabled (max {MaxDumpFrames} frames) -> {dumpDir}");
            }
        }

        /// <summary>Login handle used by the SD-card and alarm command channels.</summary>
        public uint Ticket => authTicket;

        /// <summary>Stream size reported by the camera at login.</summary>
        public int Width => frameWidth;
        public int Height => frameheight;
        public ushort DeviceVersion => deviceVersion;

        /// <summary>
        /// Raised for every assembled video access unit. <c>annexB</c> is the
        /// normalised elementary stream payload; <c>keyFrame</c> is true for
        /// IDR/SPS/PPS/VPS-bearing frames and is needed by MediaCodec to
        /// (re)start cleanly.
        /// </summary>
        public event Action<byte[], bool, VideoCodec> VideoReceived;

        public int GetAuthTicket()
        {
            try
            {
                authClient = new TcpClient();
                var r = authClient.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("[AUTH] failed connecting to socket. retrying...");
                    return 0;
                }
                authClient.EndConnect(r);
                authStream = authClient.GetStream();

                var encryptedPassword = GeneratePassword(password);
                var userBytes = Encoding.ASCII.GetBytes(username);
                var cmd1167 = new byte[520];
                WriteUInt32LE(cmd1167, 0, 1167); // command
                if (source == SourceStream.Lan)
                {
                    WriteUInt32LE(cmd1167, 4, 120); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3            
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    Array.Copy(userBytes, 0, cmd1167, 49, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 81, Math.Min(encryptedPassword.Length, 64)); //password
                }
                else
                {
                    WriteUInt32LE(cmd1167, 4, 1022); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3 
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                    Array.Copy(hostnameBytes, 0, cmd1167, 17, Math.Min(hostnameBytes.Length, 50)); //hostname
                    WriteUInt32LE(cmd1167, 67, (uint)port); //port
                    Array.Copy(userBytes, 0, cmd1167, 71, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 103, Math.Min(encryptedPassword.Length, 64)); //password
                }
                if (!SendData(authStream, cmd1167))
                {
                    Console.Error.WriteLine("[AUTH] failed send request. retrying...");
                    return 0;
                }

                var resp = ReceiveData(authStream, 256);
                if (resp == null || resp.Length < 256)
                {
                    Console.Error.WriteLine("[AUTH] failed receive response. retrying...");
                    return 0;
                }

                uint respCmd = ReadUInt32LE(resp, 0);
                if (respCmd != 1168)
                {
                    Console.Error.WriteLine($"[AUTH] invalid response cmd: {respCmd} (expected 1168). retrying...");
                    return 0;
                }
                uint loginResult = ReadUInt32LE(resp, 4);
                if (loginResult != 1001)
                {
                    if (loginResult == 1011)
                        Console.Error.WriteLine($"[AUTH] invalid username. exiting...");
                    else if (loginResult == 1012)
                        Console.Error.WriteLine($"[AUTH] invalid password. exiting...");
                    else if (loginResult == 1018)
                        Console.Error.WriteLine($"[AUTH] invalid device id. exiting...");
                    else
                        Console.Error.WriteLine($"[AUTH] login failed result: {loginResult} (expected 1001). exiting...");

                    return -1;
                }

                uint resultValue = ReadUInt32LE(resp, 8);
                byte version = resp[12];
                uint ticket = ReadUInt32LE(resp, 13);
                uint session = ReadUInt32LE(resp, 17);
                byte deviceType = resp[21];
                byte camType = resp[22];
                uint vendorId = ReadUInt16LE(resp, 23);
                uint isDomainExists = resp[25];
                byte[] domainBytes = new byte[32];
                Array.Copy(resp, 26, domainBytes, 0, 32);
                string domain = Encoding.ASCII.GetString(domainBytes).TrimEnd('\0');

                LogUtils.debug($"[AUTH] response success");
                LogUtils.debug($"[AUTH] cmd: {respCmd}");
                LogUtils.debug($"[AUTH] result: {loginResult}");
                LogUtils.debug($"[AUTH] authTicket: {ticket} (0x{ticket:X})");
                LogUtils.debug($"[AUTH] resultValue: {resultValue}");
                LogUtils.debug($"[AUTH] version: {version}");
                LogUtils.debug($"[AUTH] session: {session}");
                LogUtils.debug($"[AUTH] deviceType: {deviceType}");
                LogUtils.debug($"[AUTH] camType: {camType}");
                LogUtils.debug($"[AUTH] vendorId: {vendorId}");
                LogUtils.debug($"[AUTH] isDomainExists: {isDomainExists}");
                LogUtils.debug($"[AUTH] domain: {domain}");

                deviceVersion = version;
                sessionId = session;
                authTicket = ticket;

                Console.Error.WriteLine($"[AUTH] success ticket={authTicket} deviceVersion={deviceVersion}");
                authStream.Close(); authClient.Close();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AUTH] Error: {ex.Message}");
                return 0;
            }
            finally
            {
                authStream?.Close(); authClient?.Close();
            }
        }


        public bool StreamLogin()
        {
            streamClient = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 0x8000
            };
            var r = streamClient.BeginConnect(ip, port, null, null);
            if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("[STREAM] failed connecting to socket. retrying...");
                return false;
            }
            ;
            streamClient.EndConnect(r);
            streamStream = streamClient.GetStream();

            var cmd301 = new byte[256];
            WriteUInt32LE(cmd301, 0, 301);
            if (source == SourceStream.Lan)
            {
                WriteUInt32LE(cmd301, 4, deviceId); //device id
                WriteUInt32LE(cmd301, 8, 0); //unknown1 
                WriteUInt16LE(cmd301, 12, 20); //unknown2 
                WriteUInt32LE(cmd301, 14, authTicket); //auth ticket
                WriteUInt32LE(cmd301, 22, 4097); //audio 4096=off, 4097=on
                WriteUInt32LE(cmd301, 26, (uint)streamQuality);    //quality  0=SD, 1=HD
            }
            else
            {
                WriteUInt32LE(cmd301, 4, 1022); //unknown1 
                var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                Array.Copy(hostnameBytes, 0, cmd301, 8, Math.Min(hostnameBytes.Length, 50)); //hostname
                WriteUInt32LE(cmd301, 58, (uint)port); //port
                WriteUInt32LE(cmd301, 62, deviceId); //device id
                WriteUInt32LE(cmd301, 66, authTicket); // auth ticket
                WriteUInt32LE(cmd301, 70, sessionId); // session id
                WriteUInt32LE(cmd301, 74, (uint)streamQuality); //quality 0=SD, 1=HD
                cmd301[78] = 20; //unknown2 
                WriteUInt32LE(cmd301, 79, 1); //unknown23
            }

            if (!SendData(streamStream, cmd301))
            {
                Console.Error.WriteLine("[STREAM] login failed send request");
                return false;
            }

            var resp401 = ReceiveData(streamStream, 412);

            if (resp401 == null || resp401.Length < 8)
            {
                Console.Error.WriteLine("[STREAM] login failed receive response");
                return false;
            }
            var respCmd = ReadUInt32LE(resp401, 0);
            if (respCmd != 401)
            {
                Console.Error.WriteLine($"[STREAM] login invalid response cmd: {respCmd} (expected 401)");
                return false;
            }

            int result = (int)ReadUInt32LE(resp401, 4);
            if (result == -11 || result == -12)
            {
                Console.Error.WriteLine($"[STREAM] login failed result={result}");
                return false;
            }

            LogUtils.debug($"[STREAM] login response success");
            LogUtils.debug($"[STREAM] login cmd: {respCmd}");
            if (source == SourceStream.Lan)
            {
                ushort version = ReadUInt16LE(resp401, 8);
                uint width = ReadUInt32LE(resp401, 10);
                uint height = ReadUInt32LE(resp401, 14);
                uint maxPackSize = ReadUInt32LE(resp401, 18);
                byte audioFreq = resp401[22];
                byte audioBits = resp401[23];
                byte audioChannels = resp401[24];
                communicationVersion = version;
                frameWidth = (int)width;
                frameheight = (int)height;
                LogUtils.debug($"[STREAM] login result: {result}");
                LogUtils.debug($"[STREAM] login version: {version}");
                LogUtils.debug($"[STREAM] login width: {width}");
                LogUtils.debug($"[STREAM] login height: {height}");
                LogUtils.debug($"[STREAM] login maxPackSize: {maxPackSize}");
                LogUtils.debug($"[STREAM] login audioFreq: {audioFreq}");
                LogUtils.debug($"[STREAM] login audioBits: {audioBits}");
                LogUtils.debug($"[STREAM] login audioChannels: {audioChannels}");
            }

            if (deviceVersion > 30) GenerateMediaKey(authTicket);
            Console.Error.WriteLine($"[STREAM] login OK quality={(streamQuality == 0 ? "SD" : "HD")}");
            return true;
        }

        public bool StartStream()
        {
            Console.Error.WriteLine($"[STREAM] starting stream...");
            var cmd303 = new byte[256];
            WriteUInt32LE(cmd303, 0, 303);
            WriteUInt16LE(cmd303, 4, 0x3001);
            return SendData(streamStream, cmd303);
        }

        /// <summary>
        /// Pumps the media socket until the connection drops or <paramref name="ct"/>
        /// is cancelled. Frames are published through <see cref="VideoReceived"/>
        /// and <see cref="AudioReceived"/>.
        /// </summary>
        public void ReceiveFrames(CancellationToken ct)
        {
            bool needDecrypt = deviceVersion > 30;

            var frameFrags = new List<byte>();
            ushort frameTotal = 0;
            ushort nextFragment = 0;
            byte frameStartType = 0;
            bool assemblingFrame = false;

            var header12 = new byte[12];
            var payloadBuf = new byte[65536];

            Console.Error.WriteLine($"[RECV] decrypt={needDecrypt} communicationVersion={communicationVersion}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (needReconnect)
                    {
                        Console.Error.WriteLine($"[STREAM] lost, reconnecting... ");
                        try { streamStream?.Close(); streamClient?.Close(); } catch { }
                        if (!StreamLogin()) break;
                        if (!StartStream()) break;
                        needReconnect = false;
                    }

                    // 12-byte fragment header
                    if (ReadExact(streamStream, header12, 0, 12) < 12) continue;

                    if (header12[0] != 0x7F)
                    {
                        continue;
                    }

                    byte type = header12[1];
                    ushort totalFrame = ReadUInt16LE(header12, 3);
                    ushort curFrame = ReadUInt16LE(header12, 5);
                    ushort payLen = ReadUInt16LE(header12, 7);

                    if (payLen == 0 || payLen > 20000 || totalFrame == 0 || curFrame >= totalFrame)
                    {
                        Console.Error.WriteLine($"[SKIP] invalid header type=0x{type:X2} total={totalFrame} cur={curFrame} len={payLen}");
                        continue;
                    }

                    if (payloadBuf.Length < payLen) payloadBuf = new byte[payLen];
                    if (ReadExact(streamStream, payloadBuf, 0, payLen) < payLen) continue;

                    if (mode == OutputMode.Control)
                    {
                        // Control-only mode: the camera still pushes its normal stream over
                        // this socket, so we must keep draining fragments to avoid stalling
                        // the connection, but nothing here consumes video/audio - skip
                        // decrypt/reassembly/codec detection entirely instead of paying for it.
                        continue;
                    }

                    if (type == 0x5B)
                    {
                        continue;
                    }

                    if (curFrame == 0 || !assemblingFrame || totalFrame != frameTotal || curFrame != nextFragment)
                    {
                        if (assemblingFrame && curFrame != 0)
                        {
                            LogUtils.debug($"[FRAME] resync from type=0x{frameStartType:X2} expected={nextFragment} got={curFrame} total={totalFrame}");
                        }

                        frameFrags.Clear();
                        frameTotal = totalFrame;
                        nextFragment = 0;
                        frameStartType = type;
                        assemblingFrame = true;
                    }

                    for (int i = 0; i < payLen; i++) frameFrags.Add(payloadBuf[i]);
                    nextFragment = (ushort)(curFrame + 1);

                    if (curFrame != totalFrame - 1) continue;
                    if (frameFrags.Count < 16)
                    {
                        frameFrags.Clear();
                        assemblingFrame = false;
                        continue;
                    }

                    byte[] full = frameFrags.ToArray();
                    frameFrags.Clear();
                    assemblingFrame = false;

                    if (!HandleAsMediaFrame(frameStartType, full, needDecrypt, frameTotal, curFrame))
                    {
                        Console.Error.WriteLine($"[FRAME] unknown type=0x{frameStartType:X2} len={full.Length}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[RECV] Operation cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RECV] {ex.Message}");
                Console.Error.WriteLine($"[RECV] {ex.StackTrace}");
            }
        }

        private bool HandleAsMediaFrame(byte rawType, byte[] full, bool needDecrypt, ushort frameTotal, int curFrame)
        {
            ushort outerFrameType = full.Length >= 6 ? ReadUInt16LE(full, 4) : (ushort)0;
            uint outerFrameId = full.Length >= 4 ? ReadUInt32LE(full, 0) : 0;
            ushort outerFrameRate = full.Length >= 8 ? ReadUInt16LE(full, 6) : (ushort)0;
            ulong outerTimestamp = full.Length >= 16 ? ReadUInt64LE(full, 8) : 0;

            if (rawType == 0x1A)
            {
                if (full.Length < 16) return false;

                uint frameId = ReadUInt32LE(full, 0);
                ushort frameType = ReadUInt16LE(full, 4);
                ushort frameRate = ReadUInt16LE(full, 6);
                ulong timestamp = ReadUInt64LE(full, 8);

                byte[] payload = new byte[full.Length - 16];
                Array.Copy(full, 16, payload, 0, payload.Length);

                if (needDecrypt)
                {
                    if (communicationVersion == 21)
                        DecryptMediaPre2k(payload, payload.Length, 1);
                    else
                        DecryptAudioFrame(payload, payload.Length);
                }

                var audioFrame = new FrameData
                {
                    RawType = rawType,
                    FrameId = frameId,
                    FrameType = frameType,
                    FrameRate = frameRate,
                    Timestamp = timestamp,
                    Payload = payload
                };

                AudioReceived?.Invoke(payload);

                return true;
            }

            if (rawType == 0x16)
            {
                byte[] audioPayload = ExtractAudioPayload(full, needDecrypt);
                if (audioPayload.Length == 0) return false;

                AudioReceived?.Invoke(audioPayload);

                return true;
            }

            byte[] normalizedPayload;

            // Classic traditional frame types (0x00 I-frame / 0x01 P-frame) are the
            // proven path used by deviceVersion=31/H.264 cameras (see reference
            // V380Decoder). Use the exact fixed-offset, no-fallback pipeline instead
            // of the multi-candidate heuristics below: those heuristics are needed
            // for newer/unknown frame types (H.265, 3-lens, external variants) whose
            // header layout isn't known ahead of time, but applying them to the
            // classic path let a misaligned decrypt candidate with an accidental
            // start code outscore the real payload, corrupting slice data while
            // SPS/PPS-looking bytes still happened to show up.
            if (rawType == 0x00 || rawType == 0x01)
            {
                if (!TryExtractClassicVideoPayload(full, needDecrypt, rawType, frameTotal, curFrame, out normalizedPayload))
                    return false;
            }
            else
            {
                if (!TryExtractVideoPayload(full, needDecrypt, out normalizedPayload))
                {
                    LogUtils.debug($"[FRAME] unclassified type=0x{rawType:X2} len={full.Length} head={BitConverter.ToString(full, 0, Math.Min(16, full.Length))}");
                    return false;
                }

                MaybeDumpFrame("heuristic", full, null, null, normalizedPayload);
            }

            var codec = DetectVideoCodec(normalizedPayload);
            bool isKeyFrame = IsKeyVideoFrame(rawType, outerFrameType, normalizedPayload, out string keyReason);

            LogUtils.debug($"[VIDEO] rawType=0x{rawType:X2} total={frameTotal} cur={curFrame} codec={codec} keyframe={isKeyFrame} reason=\"{keyReason}\" nalTypes=[{string.Join(",", CollectNalTypes(normalizedPayload))}]");

            VideoReceived?.Invoke(normalizedPayload, isKeyFrame, codec);

            return true;
        }

        private byte[] ExtractAudioPayload(byte[] full, bool needDecrypt)
        {
            byte[] payload;

            if (full.Length > 20)
            {
                payload = new byte[full.Length - 20];
                Array.Copy(full, 20, payload, 0, payload.Length);
            }
            else
            {
                payload = (byte[])full.Clone();
            }

            if (needDecrypt && payload.Length >= 16)
            {
                if (communicationVersion == 21)
                    DecryptMediaPre2k(payload, payload.Length, 1);
                else
                    DecryptAudioFrame(payload, payload.Length);
            }

            return payload;
        }

        // Strict path for the classic V380 frame types (0x00 I-frame / 0x01
        // P-frame), matching the proven reference V380Decoder byte-for-byte:
        // the inner frame header is always exactly 16 bytes, decryption (when
        // needed) starts right at offset 0 of what follows, and the resulting
        // plaintext MUST begin with an Annex-B start code. There is no offset
        // search and no fallback to the multi-candidate heuristics below: if
        // the start code isn't exactly where the proven protocol puts it, the
        // frame is corrupt (wrong key, dropped bytes, desync) and must be
        // dropped rather than "rescued" by picking a plausible-looking
        // candidate elsewhere in the buffer.
        private bool TryExtractClassicVideoPayload(byte[] full, bool needDecrypt, byte rawType, ushort frameTotal, int curFrame, out byte[] normalizedPayload)
        {
            const int headerSize = 16;
            normalizedPayload = Array.Empty<byte>();

            if (full.Length <= headerSize)
            {
                LogUtils.debug($"[VIDEO] rawType=0x{rawType:X2} total={frameTotal} cur={curFrame} classic path rejected: assembledLen={full.Length} <= header {headerSize}");
                return false;
            }

            byte[] payload = new byte[full.Length - headerSize];
            Array.Copy(full, headerSize, payload, 0, payload.Length);

            bool wantDump = dumpFramesEnabled && dumpFrameCounter < MaxDumpFrames;
            byte[] preDecrypt = wantDump ? (byte[])payload.Clone() : null;

            LogUtils.debug($"[VIDEO] rawType=0x{rawType:X2} total={frameTotal} cur={curFrame} assembledLen={full.Length} payloadOffset={headerSize} pre-decrypt[0..32]={HexPrefix(payload)}");

            string decryptMethod = "none (not encrypted, deviceVersion<=30)";
            if (needDecrypt)
            {
                if (communicationVersion == 21)
                {
                    DecryptMediaPre2k(payload, payload.Length, 1);
                    decryptMethod = "DecryptMediaPre2k";
                }
                else
                {
                    DecryptVideoFrame(payload, payload.Length);
                    decryptMethod = "DecryptVideoFrame (AES-ECB, 64-of-80 skip)";
                }
            }

            LogUtils.debug($"[VIDEO] decryptMethod={decryptMethod} post-decrypt[0..32]={HexPrefix(payload)}");

            if (payload.Length < 4 || payload[0] != 0 || payload[1] != 0 || payload[2] != 0 || payload[3] != 1)
            {
                Console.Error.WriteLine($"[VIDEO] classic path: bad start code at offset 0 (expected 00-00-00-01), rawType=0x{rawType:X2} len={payload.Length} bytes={HexPrefix(payload)}");
                MaybeDumpFrame("classic-reject", full, preDecrypt, payload, null);
                return false;
            }

            normalizedPayload = payload;
            MaybeDumpFrame("classic", full, preDecrypt, payload, payload);
            return true;
        }

        private bool TryExtractVideoPayload(byte[] full, bool needDecrypt, out byte[] normalizedPayload)
        {
            int bestScore = int.MinValue;
            byte[] bestPayload = null;
            string bestDesc = null;

            foreach (var cand in EnumerateVideoCandidates(full, needDecrypt))
            {
                if (!TryNormalizeVideoPayload(cand.Payload, out var candidate, out int startIndex))
                    continue;

                int score = ScoreVideoPayload(candidate, startIndex, out int nalCount);

                LogUtils.debug($"[VIDEO][candidate] offset={cand.Offset} decrypted={cand.Decrypted} startIndex={startIndex} nalCount={nalCount} score={score}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPayload = candidate;
                    bestDesc = $"offset={cand.Offset} decrypted={cand.Decrypted} startIndex={startIndex}";
                }
            }

            if (bestPayload != null)
            {
                LogUtils.debug($"[VIDEO][heuristic] selected {bestDesc} score={bestScore}");
                normalizedPayload = bestPayload;
                return true;
            }

            normalizedPayload = Array.Empty<byte>();
            return false;
        }

        private readonly struct VideoCandidate
        {
            public readonly int Offset;
            public readonly bool Decrypted;
            public readonly byte[] Payload;
            public VideoCandidate(int offset, bool decrypted, byte[] payload)
            {
                Offset = offset; Decrypted = decrypted; Payload = payload;
            }
        }

        private IEnumerable<VideoCandidate> EnumerateVideoCandidates(byte[] full, bool needDecrypt)
        {
            foreach (int offset in new[] { 0, 4, 8, 12, 16, 20 })
            {
                if (full.Length <= offset) continue;

                byte[] direct = new byte[full.Length - offset];
                Array.Copy(full, offset, direct, 0, direct.Length);

                if (needDecrypt && direct.Length >= 16)
                {
                    byte[] decrypted = (byte[])direct.Clone();
                    if (communicationVersion == 21)
                        DecryptMediaPre2k(decrypted, decrypted.Length, 1);
                    else
                        DecryptVideoFrame(decrypted, decrypted.Length);
                    yield return new VideoCandidate(offset, true, decrypted);
                }

                yield return new VideoCandidate(offset, false, direct);
            }
        }

        private bool TryNormalizeVideoPayload(byte[] payload, out byte[] normalizedPayload, out int startIndex)
        {
            int start = FindVideoStartCode(payload);
            startIndex = start;
            if (start < 0)
            {
                normalizedPayload = payload;
                return false;
            }

            if (payload[start] == 0 && payload[start + 1] == 0 && payload[start + 2] == 1)
            {
                normalizedPayload = new byte[payload.Length - start + 1];
                normalizedPayload[0] = 0;
                Array.Copy(payload, start, normalizedPayload, 1, payload.Length - start);
                return true;
            }

            normalizedPayload = new byte[payload.Length - start];
            Array.Copy(payload, start, normalizedPayload, 0, normalizedPayload.Length);
            return true;
        }

        private bool IsKeyVideoFrame(byte rawType, ushort frameType, byte[] payload, out string reason)
        {
            if (rawType == 0x00) { reason = "rawType==0x00 (classic I-frame marker)"; return true; }

            int nalOffset = payload.Length >= 4 && payload[2] == 0 && payload[3] == 1 ? 4 : 3;
            if (payload.Length <= nalOffset) { reason = "payload too short to inspect NAL header"; return false; }

            byte nalHeader = payload[nalOffset];
            int h264NalType = nalHeader & 0x1F;
            if (h264NalType == 5) { reason = "H264 IDR slice (nalType=5)"; return true; }
            if (h264NalType == 7) { reason = "H264 SPS present (nalType=7)"; return true; }
            if (h264NalType == 8) { reason = "H264 PPS present (nalType=8)"; return true; }

            int h265NalType = (nalHeader >> 1) & 0x3F;
            if (h265NalType is 19 or 20) { reason = $"H265 IDR (nalType={h265NalType})"; return true; }
            if (h265NalType is 32 or 33 or 34) { reason = $"H265 VPS/SPS/PPS (nalType={h265NalType})"; return true; }

            if (frameType == 0) { reason = "outer frameType==0 fallback"; return true; }

            reason = $"no keyframe indicators (h264NalType={h264NalType}, h265NalType={h265NalType}, frameType={frameType})";
            return false;
        }

        private int FindVideoStartCode(byte[] payload)
        {
            int limit = Math.Min(payload.Length - 4, 128);
            for (int i = 0; i <= limit; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 0 && payload[i + 3] == 1)
                {
                    if (IsLikelyNalHeader(payload, i + 4)) return i;
                }

                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 1)
                {
                    if (IsLikelyNalHeader(payload, i + 3)) return i;
                }
            }

            return -1;
        }

        private int ScoreVideoPayload(byte[] payload, int startIndex, out int nalCount)
        {
            int score = 0;
            nalCount = 0;
            int parameterSetCount = 0;
            int idrCount = 0;

            int i = 0;
            while (i < payload.Length - 4 && nalCount < 12)
            {
                int sc = FindNextStartCode(payload, i);
                if (sc < 0) break;

                int scLen = payload[sc + 2] == 1 ? 3 : 4;
                int nalStart = sc + scLen;
                if (!IsLikelyNalHeader(payload, nalStart))
                {
                    i = sc + 1;
                    score -= 20;
                    continue;
                }

                nalCount++;
                byte nalHeader = payload[nalStart];
                int h264NalType = nalHeader & 0x1F;
                int h265NalType = (nalHeader >> 1) & 0x3F;

                if (h264NalType is 7 or 8 || h265NalType is 32 or 33 or 34)
                {
                    parameterSetCount++;
                    score += 50;
                }

                if (h264NalType == 5 || h265NalType is 19 or 20)
                {
                    idrCount++;
                    score += 30;
                }

                score += 10;
                i = nalStart + 1;
            }

            if (nalCount == 0) return int.MinValue;

            // Coherence gate (avoid picking a candidate on the strength of one
            // isolated start code): a well-formed extraction/decrypt should put
            // its first NAL right at the front of the payload (startIndex <= 1,
            // allowing for the single 0x00 padding byte TryNormalizeVideoPayload
            // adds when converting a 3-byte start code to 4-byte). If the first
            // match was found deeper in the buffer, that's exactly the "accidental
            // start code inside misaligned/encrypted noise" failure mode, so
            // require at least one more coherent NAL to corroborate it.
            if (startIndex > 1 && nalCount < 2)
                return int.MinValue;

            score += nalCount * 5;
            score += parameterSetCount * 40;
            score += idrCount * 20;
            if (startIndex <= 1) score += 100;

            return score;
        }

        private int FindNextStartCode(byte[] payload, int from)
        {
            for (int i = from; i + 3 < payload.Length; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0)
                {
                    if (payload[i + 2] == 1) return i;
                    if (payload[i + 2] == 0 && payload[i + 3] == 1) return i;
                }
            }

            return -1;
        }

        private bool IsLikelyNalHeader(byte[] payload, int index)
        {
            if (index >= payload.Length) return false;

            byte nalHeader = payload[index];
            int h264NalType = nalHeader & 0x1F;
            if (h264NalType is > 0 and < 24) return true;

            int h265NalType = (nalHeader >> 1) & 0x3F;
            return h265NalType is > 0 and < 48;
        }

        // Root cause of the camera-114727266 RTSP failure: an ordinary H265 slice
        // NAL (e.g. TRAIL_R, nal_unit_type=1, first byte 0x02) also satisfies the
        // "valid H264 nal type 1..23" test on its low 5 bits (0x02 & 0x1F == 2).
        // Guessing the codec fresh from just the first NAL's header byte on every
        // frame therefore flip-flops: the true H265 keyframe (which carries an
        // unambiguous VPS/SPS/PPS) gets detected correctly, but every following
        // H265 P-frame gets misdetected as H264 because that check runs first.
        // RtspServer.PushVideo routes frames to the H265->H264 transcoder based on
        // Codec, so this flip-flopping sent ~98% of this camera's real H265 frames
        // straight to the RTSP/RTP path mislabeled as H264, corrupting the stream
        // (confirmed live: 6556/6705 frames misdetected, matching ffprobe's "data
        // partitioning is not implemented" / spurious NAL type errors). Once a
        // stream shows an unambiguous parameter-set NAL (H264 SPS/PPS, or H265
        // VPS/SPS/PPS), lock the codec for the rest of the session instead of
        // re-guessing per frame from an inherently ambiguous slice byte.
        private VideoCodec? lockedVideoCodec = null;

        private VideoCodec DetectVideoCodec(byte[] payload)
        {
            if (payload == null || payload.Length < 5) return lockedVideoCodec ?? VideoCodec.Unknown;

            VideoCodec? unambiguous = FindUnambiguousCodec(payload);
            if (unambiguous.HasValue)
            {
                if (lockedVideoCodec.HasValue && lockedVideoCodec.Value != unambiguous.Value)
                    LogUtils.debug($"[VIDEO] codec changed from {lockedVideoCodec} to {unambiguous} (new parameter-set NAL seen)");

                lockedVideoCodec = unambiguous;
                return unambiguous.Value;
            }

            if (lockedVideoCodec.HasValue)
                return lockedVideoCodec.Value;

            // No parameter-set NAL observed yet this session: fall back to the
            // best-effort single-NAL-byte guess (may be wrong for a lone H265
            // slice, but there is nothing better before any SPS/PPS/VPS arrives).
            int offset = payload[2] == 1 ? 3 : 4;
            if (payload.Length <= offset) return VideoCodec.Unknown;

            byte nalHeader = payload[offset];
            int h264NalType = nalHeader & 0x1F;
            if (h264NalType is > 0 and < 24)
                return VideoCodec.H264;

            int h265NalType = (nalHeader >> 1) & 0x3F;
            if (h265NalType is > 0 and < 48)
                return VideoCodec.H265;

            return VideoCodec.Unknown;
        }

        // Scans every NAL in the payload for an unambiguous parameter-set marker
        // (H264 SPS=7/PPS=8, or H265 VPS=32/SPS=33/PPS=34). Unlike a lone slice
        // NAL's type field, these markers don't alias between the two codecs'
        // bit layouts, so whichever one is found settles the codec for real.
        private VideoCodec? FindUnambiguousCodec(byte[] payload)
        {
            int i = 0;
            int seen = 0;
            while (i < payload.Length - 4 && seen < 12)
            {
                int sc = FindNextStartCode(payload, i);
                if (sc < 0) break;

                int scLen = payload[sc + 2] == 1 ? 3 : 4;
                int nalStart = sc + scLen;
                if (!IsLikelyNalHeader(payload, nalStart))
                {
                    i = sc + 1;
                    continue;
                }

                seen++;
                byte h = payload[nalStart];
                int h264NalType = h & 0x1F;
                int h265NalType = (h >> 1) & 0x3F;

                if (h264NalType is 7 or 8) return VideoCodec.H264;
                if (h265NalType is 32 or 33 or 34) return VideoCodec.H265;

                i = nalStart + 1;
            }

            return null;
        }

        private void DecryptVideoFrame(byte[] data, int length)
        {
            using var aes = Aes.Create();
            aes.Key = aesKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int offset = 0; offset + 64 <= length; offset += 80)
                for (int i = 0; i < 4; i++)
                    dec.TransformBlock(data, offset + i * 16, 16, data, offset + i * 16);
        }

        private void DecryptAudioFrame(byte[] data, int length)
        {
            int alignedSize = (length / 16) * 16;
            if (alignedSize == 0) return;

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int i = 0; i < alignedSize; i += 16)
                dec.TransformBlock(data, i, 16, data, i);
        }

        // Not tested
        private void DecryptMediaPre2k(byte[] data, int length, int mode)
        {
            int decryptLength;
            if (mode == 0 && length > 2048)
            {
                decryptLength = 2048;
            }
            else
            {
                decryptLength = (length / 16) * 16;
            }
            if (decryptLength > 0)
            {
                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using var decryptor = aes.CreateDecryptor();
                decryptor.TransformBlock(data, 0, decryptLength, data, 0);
            }
        }

        void GenerateMediaKey(uint ticket)
        {
            WriteUInt32LE(aesKey, 0, ticket);
            WriteUInt64LE(aesKey, 4, 0x618123462c14795c);
            WriteUInt32LE(aesKey, 12, 0x82800df0);
            LogUtils.debug($"[KEY] {BitConverter.ToString(aesKey).Replace("-", "")}");
        }

        byte[] GeneratePassword(string pw)
        {
            byte[] sk = Encoding.ASCII.GetBytes("macrovideo+*#!^@");
            var rng = new Random();
            byte[] rk = new byte[16];
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            for (int i = 0; i < 16; i++) rk[i] = (byte)chars[rng.Next(chars.Length)];

            var pb = Encoding.ASCII.GetBytes(pw);
            var pad = new byte[48];
            Array.Copy(pb, pad, Math.Min(pb.Length, 48));

            void Enc(byte[] key)
            {
                using var a = Aes.Create();
                a.Key = key; a.Mode = CipherMode.ECB; a.Padding = PaddingMode.None;
                using var e = a.CreateEncryptor();
                for (int i = 0; i < 48; i += 16) e.TransformBlock(pad, i, 16, pad, i);
            }
            Enc(sk); Enc(rk);

            var out_ = new byte[64];
            Array.Copy(rk, 0, out_, 0, 16);
            Array.Copy(pad, 0, out_, 16, 48);
            return out_;
        }

        bool SendData(NetworkStream s, byte[] d)
        {
            try { s.Write(d, 0, d.Length); s.Flush(); return true; }
            catch (Exception ex) { Console.Error.WriteLine($"[SEND] {ex.Message}"); return false; }
        }

        byte[] ReceiveData(NetworkStream s, int max)
        {
            var buf = new byte[max]; int tot = 0;
            var deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline)
            {
                if (s.DataAvailable || tot > 0)
                {
                    int n = s.Read(buf, tot, max - tot);
                    if (n <= 0) break;
                    tot += n;
                    if (tot >= 16) break;
                }
                else Thread.Sleep(10);
            }
            if (tot == 0) return null;
            var r = new byte[tot]; Array.Copy(buf, r, tot); return r;
        }

        int ReadExact(NetworkStream s, byte[] buf, int off, int cnt)
        {
            int tot = 0;
            var deadline = DateTime.Now.AddSeconds(3);
            while (tot < cnt)
            {
                if (DateTime.Now > deadline)
                {
                    needReconnect = true;
                    return tot;
                }
                if (!s.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int n = s.Read(buf, off + tot, cnt - tot);
                if (n <= 0) break;
                tot += n;
            }
            return tot;
        }

        void WriteUInt32LE(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        void WriteUInt16LE(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        void WriteUInt64LE(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (i * 8)); }
        uint ReadUInt32LE(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        ushort ReadUInt16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        ulong ReadUInt64LE(byte[] b, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v |= ((ulong)b[o + i]) << (i * 8); return v; }

        private static string HexPrefix(byte[] data, int count = 32)
        {
            if (data == null || data.Length == 0) return "(empty)";
            int n = Math.Min(count, data.Length);
            return BitConverter.ToString(data, 0, n).Replace("-", " ");
        }

        // For --debug logging only: lists the NAL types seen in the payload
        // (both H264 5-bit and H265 6-bit interpretations, since the classic
        // path doesn't know the codec ahead of time).
        private List<string> CollectNalTypes(byte[] payload)
        {
            var types = new List<string>();
            int i = 0;
            while (i < payload.Length - 4 && types.Count < 12)
            {
                int sc = FindNextStartCode(payload, i);
                if (sc < 0) break;
                int scLen = payload[sc + 2] == 1 ? 3 : 4;
                int nalStart = sc + scLen;
                if (nalStart >= payload.Length) break;
                byte h = payload[nalStart];
                types.Add($"h264:{h & 0x1F}/h265:{(h >> 1) & 0x3F}");
                i = nalStart + 1;
            }
            return types;
        }

        private void MaybeDumpFrame(string tag, byte[] assembled, byte[] extracted, byte[] decrypted, byte[] normalized)
        {
            if (!dumpFramesEnabled) return;

            lock (dumpLock)
            {
                if (dumpFrameCounter >= MaxDumpFrames) return;
                dumpFrameCounter++;
                int n = dumpFrameCounter;

                try
                {
                    if (assembled != null) File.WriteAllBytes(Path.Combine(dumpDir, $"frame_{n:D2}_{tag}_assembled.bin"), assembled);
                    if (extracted != null) File.WriteAllBytes(Path.Combine(dumpDir, $"frame_{n:D2}_{tag}_extracted.bin"), extracted);
                    if (decrypted != null) File.WriteAllBytes(Path.Combine(dumpDir, $"frame_{n:D2}_{tag}_decrypted.bin"), decrypted);
                    if (normalized != null) File.WriteAllBytes(Path.Combine(dumpDir, $"frame_{n:D2}_{tag}_normalized.bin"), normalized);
                    Console.Error.WriteLine($"[DUMP] saved frame {n}/{MaxDumpFrames} ({tag}) to {dumpDir}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DUMP] failed: {ex.Message}");
                }
            }
        }

        public bool PtzRight() => SendControl(V380Commands.PTZ_RIGHT);
        public bool PtzLeft() => SendControl(V380Commands.PTZ_LEFT);
        public bool PtzUp() => SendControl(V380Commands.PTZ_UP);
        public bool PtzDown() => SendControl(V380Commands.PTZ_DOWN);
        public bool PtzStop() => SendControl(V380Commands.PTZ_STOP);
        public event Action<byte[]> AudioReceived;
        public bool LightOn() => SendControl(V380Commands.LIGHT_ON);
        public bool LightOff() => SendControl(V380Commands.LIGHT_OFF);
        public bool LightAuto() => SendControl(V380Commands.LIGHT_AUTO);
        public bool ImageColor() => SendControl(V380Commands.IMAGE_COLOR);
        public bool ImageBW() => SendControl(V380Commands.IMAGE_BW);
        public bool ImageAuto() => SendControl(V380Commands.IMAGE_AUTO);
        public bool ImageFlip() => SendControl(V380Commands.IMAGE_FLIP);
        private bool SendControl(byte[] payload)
        {
            if (streamStream == null) return false;
            return SendData(streamStream, payload);
        }

        public Task<SdRecordingList> ListRecordingsAsync(DateOnly date, CancellationToken ct) => SdCardProtocol.ListAsync(ip, port, deviceId, authTicket, date, ct);

        public Task<AlarmConfig> ReadAlarmConfigAsync(CancellationToken ct) => AlarmConfigProtocol.ReadAsync(ip, port, deviceId, authTicket, ct);

        public Task WriteAlarmConfigAsync(AlarmConfig config, CancellationToken ct) => AlarmConfigProtocol.WriteAsync(ip, port, deviceId, authTicket, config, ct);

        public string GetDeviceVersion()
        {
            return deviceVersion.ToString();
        }

        public void Dispose()
        {
            authStream?.Close(); authClient?.Close();
            streamStream?.Close(); streamClient?.Close();
        }
    }
}

