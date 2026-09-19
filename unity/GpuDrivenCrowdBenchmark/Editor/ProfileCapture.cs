using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
namespace HlslPerf.Crossover {
public static class ProfileCapture {
 static System.Diagnostics.Process player; static double deadline; static string capture;
 public static void Autoconnect() {
  var windowType=typeof(Editor).Assembly.GetType("UnityEditor.ProfilerWindow") ?? typeof(ProfilerDriver).Assembly.GetType("UnityEditor.ProfilerWindow");
  if(windowType!=null) EditorWindow.GetWindow(windowType);
  ProfilerDriver.profileEditor=false; ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.GPU,true); ProfilerDriver.enabled=true;
  capture=Environment.GetEnvironmentVariable("CROSSOVER_PROFILE_RAW");
  File.WriteAllText(capture+".setup.json","{\"gpuAreaBeforeLaunch\":"+ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.GPU).ToString().ToLowerInvariant()+"}");
  var psi=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("CROSSOVER_PLAYER_PATH"),Environment.GetEnvironmentVariable("CROSSOVER_PLAYER_ARGS"));
  psi.UseShellExecute=false;psi.CreateNoWindow=true;psi.WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden;
  player=System.Diagnostics.Process.Start(psi); deadline=EditorApplication.timeSinceStartup+120; EditorApplication.update+=Poll;
 }
 static void Poll() {
  if(!player.HasExited && EditorApplication.timeSinceStartup<deadline) return;
  if(!player.HasExited) player.Kill();
  ProfilerDriver.enabled=false;ProfilerDriver.SaveProfile(capture);EditorApplication.update-=Poll;EditorApplication.Exit(0);
 }

 public static void InspectMany() {
  foreach(var raw in Environment.GetEnvironmentVariable("CROSSOVER_PROFILE_INPUTS").Split(';')) {
   Environment.SetEnvironmentVariable("CROSSOVER_PROFILE_RAW",raw);Environment.SetEnvironmentVariable("CROSSOVER_PROFILE_OUTPUT",raw+".hierarchy.csv");Inspect();
  }
 }
 public static void Inspect() {
  string output=Environment.GetEnvironmentVariable("CROSSOVER_PROFILE_OUTPUT");
  File.WriteAllText(output+".api.txt",string.Join("\n", typeof(ProfilerDriver).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Select(m=>m.ToString())));
  string raw=Environment.GetEnvironmentVariable("CROSSOVER_PROFILE_RAW");
  if(string.IsNullOrEmpty(raw)) return;
  ProfilerDriver.ClearAllFrames(); ProfilerDriver.LoadProfile(raw,false);
  using(var w=new StreamWriter(output)) {
   w.WriteLine("frame,thread,name,gpuMs");
   for(int f=ProfilerDriver.firstFrameIndex;f<=ProfilerDriver.lastFrameIndex;f++) {
    for(int thread=0;thread<32;thread++) using(var data=ProfilerDriver.GetHierarchyFrameDataView(f,thread,UnityEditor.Profiling.HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,-1,false)) {
     if(!data.valid) continue;
     Walk(w,data,f,data.GetRootItemID());
    }
   }
  }
 }
 static void Walk(StreamWriter w,UnityEditor.Profiling.HierarchyFrameDataView d,int frame,int id) {
  var name=d.GetItemName(id); if(name.StartsWith("Crossover/")) w.WriteLine(frame+","+d.threadIndex+","+name+","+d.GetItemColumnDataAsFloat(id,8).ToString(System.Globalization.CultureInfo.InvariantCulture));
  var children=new System.Collections.Generic.List<int>();d.GetItemChildren(id,children); foreach(int c in children) Walk(w,d,frame,c);
 }
}
}
