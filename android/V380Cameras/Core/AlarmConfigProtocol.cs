#nullable disable

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace V380Decoder.src;

// Alarm settings for the legacy "setting version 3" firmware.
//
// Read  (command 321): 256-byte response, command+100 = 421, result at 4.
// Write (command 322): 256-byte request, 32-byte response, command+100 = 422.
//
// The request carries the 4-byte login ticket at offset 9 while the response
// does not, so the read payload starts at 9 and the write payload at 13. The
// read payload is kept verbatim and copied back on write, which keeps every
// field the camera reported intact. The only transformed field is the motion
// grid: the response puts the grid dimensions at 76/77 before the cells at
// 78, while the request has no dimensions and starts the cells at 80.
public sealed record AlarmWindow(int Number, bool Enabled, int StartHour, int StartMinute, int StartSecond, int EndHour, int EndMinute, int EndSecond);

public sealed record AlarmConfig(
    bool HasAlarmConfig,
    bool HasVoicePrompts,
    bool VoicePromptsMainSwitch,
    bool AlarmVoiceSwitch,
    bool HasExternalIo,
    bool CanSetTimeAndArea,
    bool MainAlarmSwitch,
    bool MotionAlarmSwitch,
    bool PirAlarmSwitch,
    bool SmokeAlarmSwitch,
    int Language,
    int IoMode,
    int CustomTimeCount,
    AlarmWindow Window1,
    AlarmWindow Window2,
    AlarmWindow Window3,
    int Rows,
    int Columns,
    IReadOnlyList<int> Cells)
{
    // Raw payload from the read response. Used to write back unchanged fields.
    [JsonIgnore]
    public byte[] RawPayload { get; init; } = [];

    public AlarmConfig WithSwitches(bool? main = null, bool? motion = null) => this with
    {
        MainAlarmSwitch = main ?? MainAlarmSwitch,
        MotionAlarmSwitch = motion ?? MotionAlarmSwitch
    };

    public AlarmConfig WithCells(IReadOnlyList<int> cells)
    {
        if (cells.Count != Rows * Columns) throw new ArgumentException($"Expected {Rows * Columns} cells, got {cells.Count}");
        return this with { Cells = cells.ToArray() };
    }
}

public static class AlarmConfigProtocol
{
    private const int BufferSize = 256;
    private const ushort CommandRead = 321;
    private const ushort CommandWrite = 322;

    // Payload offsets shared by request and response.
    private const int OffsetHasAlarmConfig = 24;
    private const int OffsetMainAlarmSwitch = 28;
    private const int OffsetMotionAlarmSwitch = 32;
    private const int OffsetPirAlarmSwitch = 36;
    private const int OffsetSmokeAlarmSwitch = 40;
    private const int OffsetCanSetTimeAndArea = 44;
    private const int OffsetCustomTimeCount = 45;
    private const int OffsetWindow1 = 46;
    private const int OffsetWindow2 = 53;
    private const int OffsetWindow3 = 60;
    private const int OffsetRows = 67;
    private const int OffsetColumns = 68;
    private const int OffsetCells = 69;

    // Read payload offset -> absolute response offset.
    private const int ReadPayloadStart = 9;

    // Write payload offset -> absolute request offset.
    private const int WritePayloadStart = 13;

    // The request has no grid dimensions, so its cells start two payload
    // bytes before the response's.
    private const int WriteCellOffset = OffsetCells - 2;

    public static async Task<AlarmConfig> ReadAsync(string host, int port, uint deviceId, uint ticket, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        var stream = client.GetStream();

        var request = new byte[BufferSize];
        Put16(request, 0, CommandRead);
        Put32(request, 4, deviceId);
        request[8] = 1;
        Put32(request, 9, ticket);
        await stream.WriteAsync(request, ct);

        var response = new byte[BufferSize];
        await stream.ReadExactlyAsync(response, ct);
        Validate(response, CommandRead);

        var payload = response.AsSpan(ReadPayloadStart).ToArray();
        bool Switch(int offset) => Get32(payload, offset) == 1000;

        int rows = payload[OffsetRows];
        int columns = payload[OffsetColumns];
        int cells = Math.Clamp(rows * columns, 0, payload.Length - OffsetCells);
        var grid = new int[cells];
        for (int i = 0; i < cells; i++) grid[i] = payload[OffsetCells + i];

        return new AlarmConfig(
            HasAlarmConfig: Switch(OffsetHasAlarmConfig),
            HasVoicePrompts: Switch(0),
            VoicePromptsMainSwitch: Switch(4),
            AlarmVoiceSwitch: Switch(8),
            HasExternalIo: Switch(16),
            CanSetTimeAndArea: payload[OffsetCanSetTimeAndArea] != 0,
            MainAlarmSwitch: Switch(OffsetMainAlarmSwitch),
            MotionAlarmSwitch: Switch(OffsetMotionAlarmSwitch),
            PirAlarmSwitch: Switch(OffsetPirAlarmSwitch),
            SmokeAlarmSwitch: Switch(OffsetSmokeAlarmSwitch),
            Language: (int)Get32(payload, 12),
            IoMode: (int)Get32(payload, 20),
            CustomTimeCount: payload[OffsetCustomTimeCount],
            Window1: Window(payload, 1, OffsetWindow1),
            Window2: Window(payload, 2, OffsetWindow2),
            Window3: Window(payload, 3, OffsetWindow3),
            Rows: rows,
            Columns: columns,
            Cells: grid)
        { RawPayload = payload };
    }

    public static async Task WriteAsync(string host, int port, uint deviceId, uint ticket, AlarmConfig config, CancellationToken cancellationToken = default)
    {
        if (config.RawPayload.Length == 0) throw new InvalidOperationException("Alarm settings must be read from the camera before writing");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;

        var request = new byte[BufferSize];
        Put16(request, 0, CommandWrite);
        Put32(request, 4, deviceId);
        request[8] = 1;
        Put32(request, 9, ticket);
        config.RawPayload.AsSpan(0, Math.Min(config.RawPayload.Length, BufferSize - WritePayloadStart))
            .CopyTo(request.AsSpan(WritePayloadStart));

        // Switch flags are 1000/1001 integers in the payload.
        void Flag(int offset, bool value) => Put32(request, WritePayloadStart + offset, value ? 1000u : 1001u);
        Flag(OffsetMainAlarmSwitch, config.MainAlarmSwitch);
        Flag(OffsetMotionAlarmSwitch, config.MotionAlarmSwitch);

        var cells = config.Cells;
        if (cells.Count != config.Rows * config.Columns) throw new ArgumentException("Cell count does not match grid size");
        for (int i = 0; i < cells.Count; i++) request[WritePayloadStart + WriteCellOffset + i] = (byte)cells[i];

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(request, ct);

        var response = new byte[32];
        await stream.ReadExactlyAsync(response, ct);
        Validate(response, CommandWrite);
    }

    private static void Validate(byte[] response, ushort requestCommand)
    {
        var command = Get16(response, 0);
        if (command != requestCommand + 100) throw new InvalidDataException($"Unexpected alarm config response {command}");
        var result = Get32(response, 4);
        if (result != 1001)
        {
            var detail = response.Length >= 12 ? Get32(response, 8) : 0;
            throw new InvalidDataException($"Alarm config rejected (result {result}, detail {detail})");
        }
    }

    private static AlarmWindow Window(byte[] payload, int number, int offset) => new(
        number,
        payload[offset] != 0,
        payload[offset + 1], payload[offset + 2], payload[offset + 3],
        payload[offset + 4], payload[offset + 5], payload[offset + 6]);

    private static void Put16(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset), (ushort)value);
    private static void Put32(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
    private static ushort Get16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));
    private static uint Get32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
    private static uint Get32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset));
}

