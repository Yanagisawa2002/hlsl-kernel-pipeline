using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
namespace HlslPerf.Crossover {
 [Serializable] public sealed class DrawProxy {
  public int viewIndex,visibleCount,partiallyClippedAgentCount;
  public double sumVisibleAgentArea,sumProjectedQuadAreaPixels,sumClippedProjectedAreaPixels;
  public string visibleIdHash;
 }
 public static class DrawDriftSupport {
  public static string Trajectory;
  public static DrawProxy[] Proxies;
  public static double UtcMs => (DateTime.UtcNow.Ticks-621355968000000000L)/10000.0;
  public static int ViewIndex(int i) { return Trajectory=="reverse"?999-i:Trajectory=="frozen"?500:i; }
  public static void Initialize(Agent[] agents,Calibration c) {
   Trajectory=Environment.GetEnvironmentVariable("DRAW_DRIFT_TRAJECTORY");
   if(Trajectory!="forward" && Trajectory!="reverse" && Trajectory!="frozen")throw new ArgumentException("Explicit diagnostic trajectory required");
   if(c.frames!=1000 || c.views.Length!=1000 || c.agentCount!=100000 || c.seed!=69501203 || c.targetVisibility!=.25)throw new ArgumentException("Frozen calibration contract");
   Proxies=new DrawProxy[1000];
   for(int i=0;i<1000;i++) {
    var v=c.views[i];var p=new DrawProxy {viewIndex=i};ulong hash=14695981039346656037UL;
    for(int id=0;id<agents.Length;id++) {
     var a=agents[id];if(!Model.Visible(a,v))continue;
     p.visibleCount++;hash=unchecked((hash^(uint)id)*1099511628211UL);
     double area=4.0*a.size*a.size;
     p.sumVisibleAgentArea+=area;
     double dx=Math.Max(0,Math.Min((double)a.x+a.size,(double)v.x+v.halfX)-Math.Max((double)a.x-a.size,(double)v.x-v.halfX));
     double dy=Math.Max(0,Math.Min((double)a.y+a.size,(double)v.y+v.halfY)-Math.Max((double)a.y-a.size,(double)v.y-v.halfY));
     if((double)a.x-a.size<(double)v.x-v.halfX || (double)a.x+a.size>(double)v.x+v.halfX || (double)a.y-a.size<(double)v.y-v.halfY || (double)a.y+a.size>(double)v.y+v.halfY)p.partiallyClippedAgentCount++;
     double pixelsPerArea=1280.0*720/(4.0*v.halfX*v.halfY);
     p.sumProjectedQuadAreaPixels+=area*pixelsPerArea;p.sumClippedProjectedAreaPixels+=dx*dy*pixelsPerArea;
    }
    if(p.visibleCount!=v.visible)throw new InvalidOperationException("Proxy/calibration count mismatch");
    p.visibleIdHash=hash.ToString("x16");Proxies[i]=p;
   }
  }
 }
}
