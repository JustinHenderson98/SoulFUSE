using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SlskFs;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); Console.WriteLine("PASS " + name); passed++; }
    catch(Exception ex) { Console.WriteLine("FAIL " + name + "\n" + ex); failed++; }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch(T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

await Test("browse/open/stat do not download; original protocol filename preserved", async () =>
{
    using var f = new Fixture(); await f.Refresh();
    Check(f.Engine.Catalog.List("/").Single() == "alice%20space", "Encoded username");
    Check(f.Engine.Catalog.List("/alice%20space/Music").Contains("one.bin"), "Tree");
    var h = f.Engine.Open(f.Path, 0); Check(f.Engine.HandleEntry(h).Size == f.Bytes.Length, "size");
    f.Engine.Release(h); Check(f.Server.Enqueues == 0, "Metadata must not enqueue");
});
await Test("concurrent reads share one transfer and wait beyond Succeeded until final event", async () =>
{
    using var f = new Fixture(); await f.Refresh();
    var h1=f.Engine.Open(f.Path,0); var h2=f.Engine.Open(f.Path,0);
    var watch=Stopwatch.StartNew();
    byte[] Read(long h) { var b=new byte[f.Bytes.Length]; Check(f.Engine.Read(h,0,b)==b.Length,"length"); return b; }
    var results=await Task.WhenAll(Task.Run(()=>Read(h1)),Task.Run(()=>Read(h2)));
    Check(results.All(b=>b.SequenceEqual(f.Bytes)),"exact bytes");
    Check(watch.ElapsedMilliseconds >= 180,"Must wait for completion event");
    Check(f.Server.Enqueues == 1,"One enqueue for concurrent readers");
    Check(f.Server.LastFilename == "Music\\one.bin","Remote filename unchanged");
    f.Engine.Release(h1);f.Engine.Release(h2);
});
await Test("completed cache survives engine restart and slskd outage",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch");
    f.Engine.Dispose();f.Server.Unavailable=true;
    using var engine=new Engine(f.Config,f.Api);
    var h=engine.Open(f.Path,0);var b=new byte[f.Bytes.Length];
    Check(engine.Read(h,0,b)==b.Length && b.SequenceEqual(f.Bytes),"offline exact bytes");engine.Release(h);
});
await Test("pin protects cache; evict protects open handles and never deletes slskd source",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("pin",f.Path),default)).Ok,"pin");
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"pinned eviction rejected");
    Check((await f.Engine.ExecuteAsync(new("unpin",f.Path),default)).Ok,"unpin");
    var h=f.Engine.Open(f.Path,0);
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"open eviction rejected");
    f.Engine.Release(h);
    Check((await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"evict");
    Check(File.Exists(f.Server.CompletedPath),"slskd original remains");
});
await Test("queued read times out without blocking listings or returning fake EOF",async()=>
{
    using var f=new Fixture(readTimeout:1);f.Server.NeverComplete=true;await f.Refresh();
    var h=f.Engine.Open(f.Path,0);
    var reading=Task.Run(()=>f.Engine.Read(h,0,new byte[4]));
    await Task.Delay(150);
    Check(f.Engine.Catalog.List("/alice%20space/Music").Length==2,"independent metadata");
    await Throws<OperationCanceledException>(async()=>await reading);
    f.Server.NeverComplete=false;
    var bytes=new byte[f.Bytes.Length];
    Check(f.Engine.Read(h,0,bytes)==bytes.Length && bytes.SequenceEqual(f.Bytes),"Background transfer continues after timeout");
    Check(f.Server.Enqueues==1,"Timeout did not requeue");f.Engine.Release(h);
});
await Test("EOF and empty reads do not download; writes rejected",async()=>
{
    using var f=new Fixture();await f.Refresh();var h=f.Engine.Open(f.Path,0);
    Check(f.Engine.Read(h,f.Bytes.Length,new byte[2])==0,"real EOF");
    Check(f.Engine.Read(h,0,Span<byte>.Empty)==0,"zero read");
    await Throws<UnauthorizedAccessException>(()=>Task.Run(()=>f.Engine.Open(f.Path,1)));
    await Throws<ArgumentOutOfRangeException>(()=>Task.Run(()=>f.Engine.Read(h,-1,new byte[1])));
    Check(f.Server.Enqueues==0,"No transfer");f.Engine.Release(h);
});
await Test("outside-root completion path rejected",async()=>
{
    using var f=new Fixture();f.Server.UnsafePath=true;await f.Refresh();
    var r=await f.Engine.ExecuteAsync(new("fetch",f.Path),default);
    Check(!r.Ok && r.Message.Contains("outside"),"Outside root must fail");
});
await Test("symlink in completion path rejected by native openat traversal",async()=>
{
    using var f=new Fixture();f.Server.Symlink=true;await f.Refresh();
    var r=await f.Engine.ExecuteAsync(new("fetch",f.Path),default);
    Check(!r.Ok && r.Message.Contains("safely"),"Symlink must fail");
});
await Test("malformed remote browse retains prior listing",async()=>
{
    using var f=new Fixture();await f.Refresh();f.Server.BadBrowse=true;
    Check((await f.Engine.Catalog.RefreshAsync(default)).Length==1,"invalid refresh reported");
    Check(f.Engine.Catalog.FileEntry(f.Path).Size==f.Bytes.Length,"prior snapshot retained");
});
await Test("HTTP 201 with failed enqueue is not accepted",async()=>
{
    using var f=new Fixture();f.Server.FailEnqueue=true;await f.Refresh();
    var r=await f.Engine.ExecuteAsync(new("fetch",f.Path),default);
    Check(!r.Ok && r.Message.Contains("rejected"),"Inspect failed array");
});
await Test("cache budget rejects oversized files before enqueuing",async()=>
{
    using var f=new Fixture(cacheBytes:4);await f.Refresh();
    Check(!(await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"budget failure");
    Check(f.Server.Enqueues==0,"No download beyond budget");
});
await Test("LRU evicts unpinned content to admit another file",async()=>
{
    using var f=new Fixture(cacheBytes:40);await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"first file");
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path.Replace("one.bin","two.bin")),default)).Ok,"second file");
    var first=System.IO.Path.Combine(f.Config.StateDirectory,"content",f.Engine.Catalog.FileEntry(f.Path).Key);
    Check(!File.Exists(first),"old cache file evicted");
});
await Test("CLI uses disk state without a running mount",async()=>
{
    using var f=new Fixture();await f.Refresh();
    var path=System.IO.Path.Combine(f.Root,"config.json");Json.Save(path,f.Config);
    Check(await SlskFs.Program.Main([path,"list","/"])==0,"CLI local path");
});
await Test("existing HTTP transfer is reused without a second enqueue",async()=>
{
    using var f=new Fixture();await f.Refresh();
    await f.Api.EnqueueAsync(f.Engine.Catalog.FileEntry(f.Path),default);
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"existing transfer");
    Check(f.Server.Enqueues==1,"No duplicate");
});
Console.WriteLine($"{passed} passed; {failed} failed. Real FUSE mount and real Soulseek peers are not exercised here.");
return failed==0?0:1;

sealed class Fixture : IDisposable
{
    public string Root { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"slskfs-test-"+Guid.NewGuid().ToString("N"));
    public string Path=>"/alice%20space/Music/one.bin";
    public byte[] Bytes=>Server.Bytes;
    public FakeServer Server { get; }
    public Config Config { get; }
    public SlskdClient Api { get; }
    public Engine Engine { get; }
    private readonly CancellationTokenSource stop=new();
    public Fixture(int readTimeout=3,long cacheBytes=100000)
    {
        Directory.CreateDirectory(Root);
        Server=new FakeServer(System.IO.Path.Combine(Root,"downloads"));
        Environment.SetEnvironmentVariable("SLSKFS_TEST_KEY","test-only");
        Config=new Config { ApiUrl=Server.Url+"api/v0/",ApiKeyEnv="SLSKFS_TEST_KEY",Users=["alice space"],
            StateDirectory=System.IO.Path.Combine(Root,"state"),RemoteDownloadsRoot="/container/downloads",
            LocalDownloadsRoot=Server.Downloads,PollMilliseconds=30,ReadTimeoutSeconds=readTimeout,
            TransferTimeoutSeconds=10,ApiTimeoutSeconds=3,CacheBytes=cacheBytes };
        Config.Validate();Api=new(Config);Engine=new(Config,Api,stop.Token);
    }
    public async Task Refresh() { var errors=await Engine.Catalog.RefreshAsync(default);if(errors.Length>0)throw new Exception(string.Join(";",errors)); }
    public void Dispose() { stop.Cancel();Engine.Dispose();Api.Dispose();Server.Dispose();stop.Dispose();try{Directory.Delete(Root,true);}catch(IOException){} }
}

sealed class FakeServer : IDisposable
{
    private readonly HttpListener listener=new();
    private readonly CancellationTokenSource stop=new();
    private readonly object gate=new();
    private readonly Dictionary<string,(string Id,DateTime Started)> transfers=new();
    public string Url { get; }
    public string Downloads { get; }
    public string CompletedPath=>System.IO.Path.Combine(Downloads,"one.bin");
    public byte[] Bytes { get; }=Encoding.UTF8.GetBytes("0123456789 abcdefghijklmnopqrstuvwxyz");
    public int Enqueues;
    public string? LastFilename;
    public bool NeverComplete,UnsafePath,Symlink,BadBrowse,FailEnqueue,Unavailable;
    public FakeServer(string downloads)
    {
        Downloads=downloads;Directory.CreateDirectory(downloads);
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        Url=$"http://127.0.0.1:{port}/";listener.Prefixes.Add(Url);listener.Start();
        _=Task.Run(async()=> { while(!stop.IsCancellationRequested) { try {var c=await listener.GetContextAsync();_=Task.Run(()=>Handle(c));}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;} } });
    }
    private object Transfer(string filename,(string Id,DateTime Started) t)=>new { id=t.Id,username="alice space",filename,size=Bytes.Length,
        state=NeverComplete?"Queued, Remotely":"Completed, Succeeded" };
    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var request=context.Request;var response=context.Response;
            if(request.Headers["X-API-Key"]!="test-only"){response.StatusCode=401;response.Close();return;}
            if(Unavailable){response.StatusCode=503;response.Close();return;}
            var path=Uri.UnescapeDataString(request.Url!.AbsolutePath);
            object result;
            if(path=="/api/v0/users/alice space/browse")
                result=new { directories=new[]{new{name=BadBrowse?"..\\escape":"Music",files=new[]{new{filename="one.bin",size=Bytes.Length},new{filename="two.bin",size=Bytes.Length}}}},lockedDirectories=Array.Empty<object>()};
            else if(path=="/api/v0/transfers/downloads/alice space" && request.HttpMethod=="POST")
            {
                using var doc=await JsonDocument.ParseAsync(request.InputStream);
                LastFilename=doc.RootElement[0].GetProperty("filename").GetString()!;
                CheckRequest(doc.RootElement[0].GetProperty("size").GetInt64()==Bytes.Length);
                Interlocked.Increment(ref Enqueues);response.StatusCode=201;
                if(FailEnqueue)result=new{enqueued=Array.Empty<object>(),failed=new[]{new{filename=LastFilename,message="Rejected"}}};
                else { lock(gate) {transfers[LastFilename]=(Guid.NewGuid().ToString(),DateTime.UtcNow);result=new{enqueued=new[]{Transfer(LastFilename,transfers[LastFilename])},failed=Array.Empty<object>()};} }
            }
            else if(path=="/api/v0/transfers/downloads/alice space")
            {
                lock(gate)
                {
                    if(transfers.Count==0){response.StatusCode=404;response.Close();return;}
                    result=new{username="alice space",directories=new[]{new{directory="Music",files=transfers.Select(t=>Transfer(t.Key,t.Value)).ToArray()}}};
                }
            }
            else if(path=="/api/v0/events")
            {
                var events=new List<object>();
                lock(gate)
                foreach(var t in transfers)
                {
                    if(NeverComplete || (DateTime.UtcNow-t.Value.Started).TotalMilliseconds<220)continue;
                    var basename=t.Key.Split('\\').Last();var local=System.IO.Path.Combine(Downloads,basename);
                    if(!File.Exists(local))
                    {
                        if(Symlink){var outside=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Downloads)!,"outside.bin");File.WriteAllBytes(outside,Bytes);File.CreateSymbolicLink(local,outside);}
                        else File.WriteAllBytes(local,Bytes);
                    }
                    var data=new{version=0,localFilename=UnsafePath?"/etc/passwd":"/container/downloads/"+basename,remoteFilename=t.Key,transfer=Transfer(t.Key,t.Value)};
                    events.Add(new{id=Guid.NewGuid().ToString(),type="DownloadFileComplete",timestamp=DateTime.UtcNow,data=JsonSerializer.Serialize(data)});
                }
                result=events.Skip(int.Parse(request.QueryString["offset"]??"0")).ToArray();
            }
            else {response.StatusCode=404;response.Close();return;}
            response.ContentType="application/json";var bytes=JsonSerializer.SerializeToUtf8Bytes(result);response.ContentLength64=bytes.Length;
            await response.OutputStream.WriteAsync(bytes);response.Close();
        }
        catch(Exception) { try{context.Response.StatusCode=500;context.Response.Close();}catch{} }
    }
    private static void CheckRequest(bool condition){if(!condition)throw new Exception("Bad enqueue payload");}
    public void Dispose(){stop.Cancel();listener.Close();stop.Dispose();}
}
