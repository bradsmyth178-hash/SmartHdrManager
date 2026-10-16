using SmartHdrManager;
int passed = 0;
void Test(string name, Action test) { test(); Console.WriteLine("PASS " + name); passed++; }
void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
Test("Missing OLED never writes", () => { var l=new DisplayLease(); l.Reconcile(null,true,0,_=>throw new Exception(),()=>{}); Assert(l.Written==null); });
Test("Query failure never inferred as SDR", () => { var l=new DisplayLease(); l.Reconcile(null,false,0,_=>throw new Exception(),()=>{}); Assert(l.Actual==null); });
Test("Previous HDR is not owned or turned off on release", () => { var l=new DisplayLease(); l.Reconcile(true,true,0,_=>throw new Exception(),()=>{}); l.Reconcile(true,null,4000,_=>throw new Exception(),()=>{}); Assert(l.Written==null); });
Test("Enable and restore previous SDR", () => { var writes=new List<bool>(); var l=new DisplayLease(); bool W(bool v){writes.Add(v);return true;} l.Reconcile(false,true,0,W,()=>{}); l.Reconcile(true,null,4000,W,()=>{}); Assert(writes.SequenceEqual(new[]{true,false})); });
Test("SDR override restores original HDR", () => { var writes=new List<bool>(); var l=new DisplayLease(); bool W(bool v){writes.Add(v);return true;} l.Reconcile(true,false,0,W,()=>{}); l.Reconcile(false,null,4000,W,()=>{}); Assert(writes.SequenceEqual(new[]{false,true})); });
Test("Manual external change yields until release", () => { var l=new DisplayLease(); l.Reconcile(false,true,0,_=>true,()=>{}); l.Reconcile(false,true,5000,_=>throw new Exception(),()=>{}); Assert(l.Blocked&&l.Written==null); l.Reconcile(false,null,9000,_=>throw new Exception(),()=>{}); Assert(!l.Blocked); });
Test("Readback failure blocks repeated writes", () => { var l=new DisplayLease(); l.Reconcile(false,true,0,_=>false,()=>{}); l.Reconcile(true,true,5000,_=>throw new Exception(),()=>{}); Assert(l.Blocked); });
Test("Unavailable topology retains journal for reconnect", () => { var l=new DisplayLease(); l.Reconcile(false,true,0,_=>true,()=>{}); l.Reconcile(null,null,5000,_=>throw new Exception(),()=>{}); Assert(l.Previous==false&&l.Written==true); l.Reconcile(true,null,9000,v=>!v,()=>{}); Assert(l.Written==null); });
Test("Write-ahead checkpoint precedes native command", () => { bool saved=false; var l=new DisplayLease(); l.Reconcile(false,true,0,_=>{Assert(saved);return true;},()=>saved=true); });
Test("Debounce prevents immediate restoration flicker", () => { var l=new DisplayLease(); l.Reconcile(false,true,0,_=>true,()=>{}); l.Reconcile(true,null,1000,_=>throw new Exception(),()=>{}); Assert(l.Written==true); });
var hdr=new GameObservation("oled","Native HDR",true); var sdrLg=new GameObservation("lg","SDR",true);
Test("LG editing app cannot defeat OLED HDR game",()=>Assert(GameDemand.Decide("oled",new[]{hdr,sdrLg},false,false,false)==true));
Test("LG game does not request OLED",()=>Assert(GameDemand.Decide("oled",new[]{hdr with{Display="lg"}},false,false,false)==null));
Test("Overlapping HDR games retain demand",()=>{ Assert(GameDemand.Decide("oled",new[]{hdr,hdr},false,false,false)==true); Assert(GameDemand.Decide("oled",new[]{hdr},false,false,false)==true); });
Test("HDR wins over simultaneous SDR on same screen",()=>Assert(GameDemand.Decide("oled",new[]{hdr,sdrLg with{Display="oled"}},false,false,false)==true));
Test("Streaming and virtual exclusion",()=>Assert(GameDemand.Decide("oled",new[]{hdr},true,false,true)==null));
Test("Pause releases",()=>Assert(GameDemand.Decide("oled",new[]{hdr},false,true,true)==null));
Test("Unknown policy makes no HDR claim",()=>Assert(GameDemand.Decide("oled",new[]{hdr with{Policy="Unknown"}},false,false,false)==null));
Test("Failed launch reservation released",()=>{Assert(GameDemand.Decide("oled",Array.Empty<GameObservation>(),false,false,true)==true);Assert(GameDemand.Decide("oled",Array.Empty<GameObservation>(),false,false,false)==null);});
Test("Disabled game excluded",()=>Assert(GameDemand.Decide("oled",new[]{hdr with{Enabled=false}},false,false,false)==null));
Test("Unknown streaming log suspends",()=>Assert(StreamingEvidence.Parse("CLIENT DISCONNECTED")==StreamState.Unknown));
Test("Idle host permits local games",()=>Assert(StreamingEvidence.Parse("Configuration UI available at x")==StreamState.Idle));
Test("Overlapping streaming clients remain active",()=>Assert(StreamingEvidence.Parse("Configuration UI available at x\nCLIENT CONNECTED\nCLIENT CONNECTED\nCLIENT DISCONNECTED")==StreamState.Active));
Test("Completed stream releases guard",()=>Assert(StreamingEvidence.Parse("Configuration UI available at x\nCLIENT CONNECTED\nCLIENT DISCONNECTED")==StreamState.Idle));
Test("Explicit missing identity never falls back to primary",()=>Assert(DisplaySelection.Match(new[]{("lg",1)},"oled",d=>d.Item1).Length==0));
Test("Reconnected display uses current target identifier",()=>{
    var before=DisplaySelection.Match(new[]{("oled",1)},"oled",d=>d.Item1);
    var after=DisplaySelection.Match(new[]{("lg",1),("oled",7)},"oled",d=>d.Item1);
    Assert(before[0].Item2==1&&after[0].Item2==7);
});
Test("Ambiguous display identity fails closed",()=>Assert(DisplaySelection.Match(new[]{("oled",1),("oled",7)},"oled",d=>d.Item1).Length==0));
Test("Restoration retry budget retains recovery ownership",()=>{
    var l=new DisplayLease(); int calls=0;
    l.Reconcile(false,true,0,_=>true,()=>{});
    for(int i=1;i<=5;i++) l.Reconcile(true,null,i*4000,_=>{calls++;return false;},()=>{});
    Assert(calls==3&&l.Written==true&&l.Previous==false);
});
Console.WriteLine($"{passed} simulated policy tests passed; no hardware writes.");
