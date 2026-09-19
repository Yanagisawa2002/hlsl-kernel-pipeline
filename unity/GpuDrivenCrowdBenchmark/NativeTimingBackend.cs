using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;
namespace HlslPerf.Crossover {
 public sealed class NativeTimingBackend {
  [DllImport("CrossoverTimestamp")] static extern IntPtr CrossoverEvent();
  [DllImport("CrossoverTimestamp")] static extern int CrossoverStatus();
  [DllImport("CrossoverTimestamp")] static extern int CrossoverReserve(ulong id);
  [DllImport("CrossoverTimestamp")] static extern int CrossoverRead(ulong id,[Out] ulong[] output);
  readonly IntPtr callback;readonly ulong[] values=new ulong[7];
  public ulong Submitted {get;private set;} public ulong Resolved {get;private set;} public int MaxOccupancy {get;private set;}
  public NativeTimingBackend(CommandBuffer commands) {callback=CrossoverEvent();commands.Clear();commands.IssuePluginEventAndData(callback,0,IntPtr.Zero);UnityEngine.Graphics.ExecuteCommandBuffer(commands);}
  public bool Ready {get {int s=CrossoverStatus();if(s<0)throw new InvalidOperationException("Native backend error "+s);return s==1;}}
  public ulong Reserve() {ulong id=Submitted+1;if(CrossoverReserve(id)!=1)throw new InvalidOperationException("Native ring busy/invalid: no wait or overwrite allowed");Submitted=id;MaxOccupancy=Math.Max(MaxOccupancy,checked((int)(Submitted-Resolved)));return id;}
  public void Stamp(CommandBuffer commands,ulong id,int stage) {commands.IssuePluginEventAndData(callback,1,new IntPtr(checked((long)(id*4+(uint)stage))));}
  public bool TryRead(out ulong[] data) {
   data=values;if(Resolved==Submitted)return false;ulong id=Resolved+1;int state=CrossoverRead(id,values);
   if(state<0)throw new InvalidOperationException("Native read error "+state);if(state==0)return false;
   if(values[0]!=id || values[1]==0 || values[1]>values[2] || values[2]>values[3] || values[4]==0 || values[5]==0 || values[6]<values[5])throw new InvalidOperationException("Invalid native ID/ticks/frequency/fence");
   Resolved=id;return true;
  }
 }
}
