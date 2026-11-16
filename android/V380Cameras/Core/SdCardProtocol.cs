#nullable disable

using System.Buffers.Binary;
using System.Net.Sockets;
namespace V380Decoder.src;
public sealed record SdRecording(uint Id, byte EventType, uint StartRaw, uint EndRaw, string StartLocal, string EndLocal, long DurationSeconds);
public sealed record SdRecordingList(string Date, string TimeBasis, bool Complete, IReadOnlyList<SdRecording> Recordings);
public static class SdCardProtocol
{
    public static async Task<SdRecordingList> ListAsync(string host, int port, uint deviceId, uint ticket, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (ticket == 0) throw new InvalidOperationException("Camera is not authenticated");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25)); var ct=deadline.Token;
        using var client=new TcpClient(); await client.ConnectAsync(host,port,ct); var stream=client.GetStream();
        var request=new byte[128]; Put32(request,0,361);Put32(request,4,deviceId);Put32(request,8,ticket);
        request[12]=0;request[13]=0; DateFields(request,14,date,0,0,0);DateFields(request,34,date,23,59,59);request[58]=1;
        await stream.WriteAsync(request,ct);
        byte[] header=new byte[32]; short status=0;
        for(int i=0;i<20;i++) {await stream.ReadExactlyAsync(header,ct);var command=Get32(header,0);if(command==2000)continue;if(command!=461&&command!=462)throw new InvalidDataException($"Unsupported SD search response {command}");status=Get16(header,4);break;}
        if(status!=1)throw new InvalidDataException($"SD search rejected ({status})");
        var next=new byte[32];Put32(next,0,362);await stream.WriteAsync(next,ct);
        var records=new List<SdRecording>();var block=new byte[512];bool complete=false;
        for(int page=0;page<2048;page++) {
            await stream.ReadExactlyAsync(block,ct);var command=Get32(block,0);if(command==460||command==2000)continue;
            if(command!=462)throw new InvalidDataException($"Unsupported SD list response {command}");
            status=Get16(block,4);int count=Get16(block,6);
            if(status!=11&&status!=12)throw new InvalidDataException($"SD list rejected ({status})");
            if(count<0||8+count*13>block.Length)throw new InvalidDataException("Invalid SD record count");
            for(int i=0;i<count;i++) {int o=8+i*13;uint id=Get32(block,o);if(id==0)continue;uint start=Get32(block,o+5),end=Get32(block,o+9);if(end<start)throw new InvalidDataException("Invalid SD time range");records.Add(new(id,block[o+4],start,end,WallTime(start),WallTime(end),(long)end-start));}
            if(status==12){complete=true;break;}
        }
        if(!complete)throw new InvalidDataException("SD list exceeded bounded page limit");
        return new(date.ToString("yyyy-MM-dd"),"camera-local wall clock; no UTC offset applied",complete,records.OrderByDescending(x=>x.StartRaw).DistinctBy(x=>(x.Id,x.StartRaw)).ToArray());
    }
    public static string WallTime(uint raw)=>DateTime.UnixEpoch.AddSeconds(raw).ToString("yyyy-MM-dd'T'HH:mm:ss");
    static void DateFields(byte[] b,int o,DateOnly d,int h,int m,int s){int[] values={d.Year,d.Month,d.Day,h,m,s};for(int i=0;i<6;i++)BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o+2*i),(ushort)values[i]);}
    static void Put32(byte[] b,int o,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o),v);
    static uint Get32(byte[] b,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    static short Get16(byte[] b,int o)=>BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o));
}

