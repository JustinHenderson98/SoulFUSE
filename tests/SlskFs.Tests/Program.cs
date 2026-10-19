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
    Check(f.Engine.Catalog.List("/").Single() == "alice space", "Readable username");
    Check(f.Engine.Catalog.List("/alice space/Music").Contains("one.bin"), "Tree");
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
await Test("pin protects cache; evict protects open handles and deletes the completed download",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("pin",f.Path),default)).Ok,"pin");
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"pinned eviction rejected");
    Check((await f.Engine.ExecuteAsync(new("unpin",f.Path),default)).Ok,"unpin");
    var h=f.Engine.Open(f.Path,0);
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"open eviction rejected");
    f.Engine.Release(h);
    Check((await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"evict");
    Check(!File.Exists(f.Server.CompletedPath),"Completed download deleted");
    f.Server.HideOldEvents=true;
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"refetch after eviction");
    Check(f.Server.Enqueues==2 && f.Server.Removes==1,"Old completed transfer removed and fresh download requested");
});
await Test("queued read times out without blocking listings or returning fake EOF",async()=>
{
    using var f=new Fixture(readTimeout:1);f.Server.NeverComplete=true;await f.Refresh();
    var h=f.Engine.Open(f.Path,0);
    var reading=Task.Run(()=>f.Engine.Read(h,0,new byte[4]));
    await Task.Delay(150);
    Check(f.Engine.Catalog.List("/alice space/Music").Length==2,"independent metadata");
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
    var first=f.Server.CompletedPath;
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
await Test("one physical copy; unknown downloads untouched",async()=>
{
    using var f=new Fixture();await f.Refresh();
    var unrelated=System.IO.Path.Combine(f.Server.Downloads,"manual.bin");File.WriteAllText(unrelated,"keep");
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch");
    Check(!Directory.Exists(System.IO.Path.Combine(f.Config.StateDirectory,"content")),"No private data copy");
    var item=Json.Load<Dictionary<string,CacheItem>>(System.IO.Path.Combine(f.Config.StateDirectory,"downloads-cache.json")).Values.Single();
    Check(item.RelativePath=="one.bin","Index points to completed download");
    Check((await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"evict");
    Check(File.ReadAllText(unrelated)=="keep","Untracked file retained");
});
await Test("external deletion triggers re-download and preserves pin",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("pin",f.Path),default)).Ok,"pin");
    File.Delete(f.Server.CompletedPath);
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"refetch");
    Check(f.Server.Enqueues==2 && f.Server.Removes==1,"Removed stale record and requeued");
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"Pin survived");
});
await Test("active reader continues after external unlink without re-download",async()=>
{
    using var f=new Fixture();await f.Refresh();var h=f.Engine.Open(f.Path,0);var b=new byte[4];
    Check(f.Engine.Read(h,0,b)==4,"first read");File.Delete(f.Server.CompletedPath);f.Server.Unavailable=true;
    Check(f.Engine.Read(h,4,b)==4 && b.SequenceEqual(f.Bytes.Skip(4).Take(4)),"Existing descriptor retained");
    Check(f.Server.Enqueues==1,"No new transfer");f.Engine.Release(h);
});
await Test("external replacement is not read or deleted",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch");
    // Keep old inode alive to avoid inode reuse during the test.
    File.Move(f.Server.CompletedPath,f.Server.CompletedPath+".old");
    File.WriteAllBytes(f.Server.CompletedPath,f.Bytes);
    Check(!(await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"Replacement read refused");
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"Replacement eviction refused");
    Check(File.Exists(f.Server.CompletedPath),"Replacement retained");
});
await Test("symlink swapped into cache cannot delete its target",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch");
    File.Move(f.Server.CompletedPath,f.Server.CompletedPath+".old");
    File.CreateSymbolicLink(f.Server.CompletedPath,f.Server.CompletedPath+".old");
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"Symlink eviction refused");
    Check(File.Exists(f.Server.CompletedPath+".old"),"Target retained");
});
await Test("legacy private cache stays untouched",async()=>
{
    using var f=new Fixture();f.Engine.Dispose();
    var legacy=System.IO.Path.Combine(f.Config.StateDirectory,"content");Directory.CreateDirectory(legacy);
    File.WriteAllText(System.IO.Path.Combine(legacy,"old-copy"),"legacy");
    File.WriteAllText(System.IO.Path.Combine(f.Config.StateDirectory,"cache.json"),"{}");
    using var engine=new Engine(f.Config,f.Api);
    Check(File.ReadAllText(System.IO.Path.Combine(legacy,"old-copy"))=="legacy","No automatic legacy deletion");
});
await Test("eviction mapping survives restart with old completion events unavailable",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch");
    Check((await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"evict");
    f.Engine.Dispose();f.Server.HideOldEvents=true;
    using var engine=new Engine(f.Config,f.Api);
    Check((await engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch after restart");
    Check(f.Server.Removes==1 && f.Server.Enqueues==2,"Persisted identity used");
});
await Test("symlink replacing download root cannot redirect eviction",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"fetch");
    var renamed=f.Server.Downloads+"-old";Directory.Move(f.Server.Downloads,renamed);
    Directory.CreateSymbolicLink(f.Server.Downloads,renamed);
    Check(!(await f.Engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"Root symlink rejected");
    Check(File.Exists(System.IO.Path.Combine(renamed,"one.bin")),"Target retained");
});
await Test("mount names preserve spaces, punctuation, Unicode and literal percent strings",async()=>
{
    Check(Paths.MountPath("alice space", @"@@szgoh\Captain Laserhawk\épisode [1] #100%.nfo") ==
        "/alice space/@@szgoh/Captain Laserhawk/épisode [1] #100%.nfo", "Readable path");
    Check(Paths.MountPath("alice", @"Music\a%20b") != Paths.MountPath("alice", @"Music\a b"),"Literal percent is not decoded");
    Check(Paths.Segment(new string('é',127)).Length==127,"UTF-8 limit, not URL length");
    await Throws<InvalidDataException>(()=>Task.Run(()=>Paths.Segment(new string('é',128))));
    await Throws<InvalidDataException>(()=>Task.Run(()=>Paths.Segment("../escape")));
    await Throws<InvalidDataException>(()=>Task.Run(()=>Paths.MountPath("alice", @"Music\..\escape")));
});
await Test("legacy catalog migrates offline without losing cache or pins",async()=>
{
    using var f=new Fixture();await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("pin",f.Path),default)).Ok,"pin");
    var entry=f.Engine.Catalog.FileEntry(f.Path);f.Engine.Dispose();
    var legacy=entry with { Path="/alice%20space/Music/one.bin" };
    Json.Save(System.IO.Path.Combine(f.Config.StateDirectory,"catalog.json"),new SavedCatalog(new(){["alice space"]=[legacy]}));
    f.Server.Unavailable=true;
    using var engine=new Engine(f.Config,f.Api);
    Check(engine.Catalog.FileEntry(f.Path).Key==entry.Key,"Cache identity unchanged");
    Check((await engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"Offline cache reuse");
    Check(!(await engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"Pin retained");
    Check(!engine.Catalog.IsDirectory("/alice%20space"),"No encoded aliases");
    var saved=Json.Load<SavedCatalog>(System.IO.Path.Combine(f.Config.StateDirectory,"catalog.json"));
    Check(saved.Users["alice space"][0].Path==f.Path,"Migration persisted");
});
await Test("catalog migration keeps literal percent sequences distinct from spaces",()=>
{
    using var f=new Fixture();f.Engine.Dispose();
    Entry Old(string name)=>new("/alice%20space/"+Uri.EscapeDataString(name),"alice space",name,1);
    Json.Save(System.IO.Path.Combine(f.Config.StateDirectory,"catalog.json"),new SavedCatalog(new(){["alice space"]=[Old("a b"),Old("a%20b")]}));
    using var engine=new Engine(f.Config,f.Api);
    var names=engine.Catalog.List("/alice space");
    Check(names.Length==2 && names.Contains("a b") && names.Contains("a%20b"),"Distinct names preserved");
    return Task.CompletedTask;
});
await Test("opaque roots hidden; protocol names and cache keys retained",()=>
{
    using var f=new Fixture();f.Engine.Dispose();
    var remotes=new[]{@"@@szgoh\Captain Laserhawk\episode.nfo", @"@@yzvjb\Books\book.pdf", @"Music\@@nested\song.flac"};
    var entries=remotes.Select(r=>new Entry(Paths.MountPath("alice space",r),"alice space",r,1)).ToArray();
    Json.Save(System.IO.Path.Combine(f.Config.StateDirectory,"catalog.json"),new SavedCatalog(new(){["alice space"]=entries}));
    using var engine=new Engine(f.Config,f.Api);
    Check(engine.Catalog.List("/alice space").SequenceEqual(new[]{"Books","Captain Laserhawk","Music"}),"Flattened roots");
    var visible=engine.Catalog.FileEntry("/alice space/Captain Laserhawk/episode.nfo");
    Check(visible.RemoteFilename==remotes[0] && visible.Key==entries[0].Key,"Protocol and cache identity retained");
    Check(engine.Catalog.IsDirectory("/alice space/Music/@@nested"),"Only leading prefix hidden");
    return Task.CompletedTask;
});
await Test("overlapping share names retain separate original roots",()=>
{
    using var f=new Fixture();f.Engine.Dispose();
    var remotes=new[]{@"@@one\Books\a.pdf", @"@@two\Books\b.pdf", @"@@three\Video\movie.bin"};
    var entries=remotes.Select(r=>new Entry(Paths.MountPath("alice space",r),"alice space",r,1)).ToArray();
    Json.Save(System.IO.Path.Combine(f.Config.StateDirectory,"catalog.json"),new SavedCatalog(new(){["alice space"]=entries}));
    using var engine=new Engine(f.Config,f.Api);
    Check(engine.Catalog.List("/alice space").Length==3,"No merge");
    foreach(var entry in entries)Check(engine.Catalog.FileEntry(entry.Path).RemoteFilename==entry.RemoteFilename,"Every file preserved");
    return Task.CompletedTask;
});
await Test("flattened file versus ordinary directory falls back without shadowing",()=>
{
    using var f=new Fixture();f.Engine.Dispose();
    var remotes=new[]{@"@@one\Books", @"Books\a.pdf"};
    var entries=remotes.Select(r=>new Entry(Paths.MountPath("alice space",r),"alice space",r,1)).ToArray();
    Json.Save(System.IO.Path.Combine(f.Config.StateDirectory,"catalog.json"),new SavedCatalog(new(){["alice space"]=entries}));
    using var engine=new Engine(f.Config,f.Api);
    Check(engine.Catalog.IsDirectory("/alice space/Books"),"Ordinary directory retained");
    Check(engine.Catalog.FileEntry("/alice space/@@one/Books").RemoteFilename==remotes[0],"Conflicting file separate");
    return Task.CompletedTask;
});
await Test("hidden prefix still used for enqueue, cache restart and pin",async()=>
{
    using var f=new Fixture();f.Server.BrowseRoot=@"@@szgoh\Music";await f.Refresh();
    Check((await f.Engine.ExecuteAsync(new("pin",f.Path),default)).Ok,"pin via visible path");
    Check(f.Server.LastFilename==@"@@szgoh\Music\one.bin","Full protocol path sent");
    f.Engine.Dispose();f.Server.Unavailable=true;
    using var engine=new Engine(f.Config,f.Api);
    Check((await engine.ExecuteAsync(new("fetch",f.Path),default)).Ok,"Offline cache reuse");
    Check(!(await engine.ExecuteAsync(new("evict",f.Path),default)).Ok,"Pin preserved");
});
await Test("friends imports existing allowlist, preserves unrelated YAML and other denies",()=>
{
    using var f=new Fixture();var path=System.IO.Path.Combine(f.Root,"slskd.yml");
    var before="soulseek:\n  password: 'keep: secret' # retain comment\n";
    var after="      members: [blocked]\n      cidrs: [192.0.2.0/24]\nshares:\n  directories: ['[Shared]/shared']\n";
    File.WriteAllText(path,before+"transfers:\n  groups:\n    blacklisted:\n      patterns:\n        - '^(?!(?:jeh24|jeh24-slskfs)$).*$'\n        - '^bot_'\n"+after);
    var config=f.Config with {SlskdConfigPath=path};
    Check(FriendsManager.Execute(config,"list",null).Ok,"list");
    Check(FriendsManager.Execute(config,"add","alice.name+test").Ok,"add");
    var text=File.ReadAllText(path);
    Check(text.StartsWith(before) && text.EndsWith(after),"Other YAML retained byte-for-byte");
    Check(text.Contains("^bot_"),"Other deny pattern retained");
    Check(JsonSerializer.SerializeToElement(FriendsManager.Execute(config,"list",null).Data).GetProperty("users").EnumerateArray().Any(n=>n.GetString()=="alice.name+test"),"Plain name persisted");
    Check(File.ReadAllText(path+".soulfuse.bak").Contains("jeh24-slskfs"),"Backup");
    Check(FriendsManager.Execute(config,"remove","JEH24").Ok,"Case-insensitive removal");
    return Task.CompletedTask;
});
await Test("friends regex escapes names and empty allowlist blocks everyone",()=>
{
    var names=new[]{"a.b","a|b","a+b","a b","O'Neil","a\\b","$anchor"};
    var regex=new System.Text.RegularExpressions.Regex(FriendsManager.Pattern(names));
    foreach(var name in names)Check(!regex.IsMatch(name),"Allowed literal "+name);
    foreach(var name in new[]{"axb","a","b","aaab","a.b\n"})Check(regex.IsMatch(name),"Blocked nonmember");
    Check(new System.Text.RegularExpressions.Regex(FriendsManager.Pattern([])).IsMatch("anyone"),"Empty blocks everyone");
    return Task.CompletedTask;
});
await Test("friends supports empty list, quoted names, CRLF and repeated no-op commands",()=>
{
    using var f=new Fixture();var path=System.IO.Path.Combine(f.Root,"slskd.yml");
    File.WriteAllText(path,"transfers:\r\n  groups:\r\n    blacklisted:\r\n      patterns: []\r\n");
    var config=f.Config with {SlskdConfigPath=path};
    Check(FriendsManager.Execute(config,"add","O'Neil|friend").Ok,"add quoted name");
    var first=File.ReadAllText(path);
    Check(first.Contains("\r\n") && first.Contains("O''Neil"),"YAML escaping and newline");
    Check(FriendsManager.Execute(config,"add","O'Neil|friend").Ok && File.ReadAllText(path)==first,"Idempotent add");
    Check(FriendsManager.Execute(config,"remove","O'Neil|friend").Ok,"remove last friend");
    Check(File.ReadAllText(path).Contains(FriendsManager.Pattern([])),"Deny-all written");
    return Task.CompletedTask;
});
await Test("friends rejects ambiguous policies and YAML anchors without modifying files",async()=>
{
    using var f=new Fixture();var path=System.IO.Path.Combine(f.Root,"slskd.yml");var config=f.Config with{SlskdConfigPath=path};
    foreach(var text in new[]{
        "transfers:\n  groups:\n    blacklisted:\n      patterns:\n        - '^(?!(?:alice)$).*$'\n        - '^(?!(?:bob)$).*$'\n",
        "transfers:\n  groups:\n    blacklisted:\n      patterns: &shared []\n"})
    {
        File.WriteAllText(path,text);
        await Throws<InvalidDataException>(()=>Task.Run(()=>FriendsManager.Execute(config,"add","charlie")));
        Check(File.ReadAllText(path)==text,"Untouched on error");
    }
});
await Test("friends CLI requires no API key or mounted engine",async()=>
{
    using var f=new Fixture();var path=System.IO.Path.Combine(f.Root,"slskd.yml");
    File.WriteAllText(path,"transfers:\n  groups:\n    blacklisted:\n      patterns: []\n");
    var config=f.Config with{SlskdConfigPath=path,ApiKeyEnv="SLSKFUSE_NO_SUCH_KEY",Users=[]};
    var cp=System.IO.Path.Combine(f.Root,"config.json");Json.Save(cp,config);
    Check(await SlskFs.Program.Main([cp,"friends","add","alice"])==0,"CLI add");
    Check(await SlskFs.Program.Main([cp,"friends","list"])==0,"CLI list");
    Check(await SlskFs.Program.Main([cp,"friends","remove","alice"])==0,"CLI remove");
});
Console.WriteLine($"{passed} passed; {failed} failed. Real FUSE mount and real Soulseek peers are not exercised here.");
return failed==0?0:1;

sealed class Fixture : IDisposable
{
    public string Root { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"slskfs-test-"+Guid.NewGuid().ToString("N"));
    public string Path=>"/alice space/Music/one.bin";
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
            DedicatedDownloadsCache=true,LocalDownloadsRoot=Server.Downloads,PollMilliseconds=30,ReadTimeoutSeconds=readTimeout,
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
    private readonly HashSet<string> materialized = new();
    public int Enqueues, Removes;
    public string? LastFilename;
    public string BrowseRoot="Music";
    public bool HideOldEvents;
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
                result=new { directories=new[]{new{name=BadBrowse?"..\\escape":BrowseRoot,files=new[]{new{filename="one.bin",size=Bytes.Length},new{filename="two.bin",size=Bytes.Length}}}},lockedDirectories=Array.Empty<object>()};
            else if(path=="/api/v0/transfers/downloads/alice space" && request.HttpMethod=="POST")
            {
                using var doc=await JsonDocument.ParseAsync(request.InputStream);
                LastFilename=doc.RootElement[0].GetProperty("filename").GetString()!;
                CheckRequest(doc.RootElement[0].GetProperty("size").GetInt64()==Bytes.Length);
                Interlocked.Increment(ref Enqueues);response.StatusCode=201;
                if(FailEnqueue)result=new{enqueued=Array.Empty<object>(),failed=new[]{new{filename=LastFilename,message="Rejected"}}};
                else { lock(gate) {transfers[LastFilename]=(Guid.NewGuid().ToString(),DateTime.UtcNow);result=new{enqueued=new[]{Transfer(LastFilename,transfers[LastFilename])},failed=Array.Empty<object>()};} }
            }
            else if(path.StartsWith("/api/v0/transfers/downloads/alice space/") && request.HttpMethod=="DELETE")
            {
                CheckRequest(request.QueryString["remove"]=="true");
                lock(gate)
                {
                    var id=path.Split('/').Last();
                    var key=transfers.FirstOrDefault(t=>t.Value.Id==id).Key;
                    if(key!=null)transfers.Remove(key);
                    Interlocked.Increment(ref Removes);
                }
                response.StatusCode=204;response.Close();return;
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
                    if((HideOldEvents && Enqueues == 1) || NeverComplete || (DateTime.UtcNow-t.Value.Started).TotalMilliseconds<220)continue;
                    var basename=t.Key.Split('\\').Last();var local=System.IO.Path.Combine(Downloads,basename);
                    if(materialized.Add(t.Value.Id))
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
