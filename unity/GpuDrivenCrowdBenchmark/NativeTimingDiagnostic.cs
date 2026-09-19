using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
namespace HlslPerf.Crossover {
[Serializable] public sealed class NativeSample {
 public int submissionUnityFrame,availabilityUnityFrame,repetitions;
 public ulong id,t0,t1,t2,frequency,requiredFence,completedFence;
 public double gpuCullMs,gpuDrawMs,gpuRangeMs;
}
[Serializable] public sealed class NativeResult {
 public int schema=3,submitted,resolved,ringCapacity=32,submissionFrames=96;
 public string sourceIdentity,pluginSha256,adapter,graphicsApi,renderingThreadingMode,unityVersion,error="";
 public bool completed,batchmode,profilerEnabled;
 public string scope="Native ID/fence availability diagnostic; not a quiet performance pilot";
 public NativeSample[] samples=new NativeSample[96];
}
public sealed class NativeTimingDiagnostic : MonoBehaviour {
 const string Dll="CrossoverTimestamp";
 [DllImport(Dll)] static extern IntPtr CrossoverEvent();
 [DllImport(Dll)] static extern int CrossoverStatus();
 [DllImport(Dll)] static extern int CrossoverReserve(ulong id);
 [DllImport(Dll)] static extern int CrossoverRead(ulong id,[Out] ulong[] output);
 Options options;NativeResult result;IntPtr callback;ComputeShader compute; Material material;
 ComputeBuffer agents,visible,args;RenderTexture target;CommandBuffer command; bool active;int drain;double deadline;
 readonly ulong[] values=new ulong[7];
 public void Initialize(Options o,ComputeShader shader,Shader drawing) {
  options=o;result=new NativeResult { sourceIdentity=Resources.Load<TextAsset>("crossover-source-identity").text.Trim(),
   pluginSha256=Resources.Load<TextAsset>("crossover-plugin-identity")?.text.Trim() ?? "unavailable",adapter=SystemInfo.graphicsDeviceName,
   graphicsApi=SystemInfo.graphicsDeviceType.ToString(),renderingThreadingMode=SystemInfo.renderingThreadingMode.ToString(),
   unityVersion=Application.unityVersion,batchmode=Application.isBatchMode,profilerEnabled=UnityEngine.Profiling.Profiler.enabled };
  try {
   if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)throw new NotSupportedException("Native diagnostic requires D3D12");
   QualitySettings.vSyncCount=0;Application.targetFrameRate=-1;Application.runInBackground=true;
   compute=shader;agents=new ComputeBuffer(100000,28);agents.SetData(Model.Generate(100000,69501203));
   visible=new ComputeBuffer(100000,4,ComputeBufferType.Append);args=new ComputeBuffer(1,16,ComputeBufferType.IndirectArguments);args.SetData(new uint[]{6,0,0,0});
   compute.SetBuffer(0,"_Agents",agents);compute.SetBuffer(0,"_VisibleAgents",visible);compute.SetInt("_AgentCount",100000);
   material=new Material(drawing);material.SetBuffer("_Agents",agents);material.SetBuffer("_VisibleAgents",visible);
   var rect=new Vector4(0,0,60,34);compute.SetVector("_ViewRect",rect);material.SetVector("_ViewRect",rect);
   target=new RenderTexture(new RenderTextureDescriptor(1280,720){graphicsFormat=UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,depthStencilFormat=UnityEngine.Experimental.Rendering.GraphicsFormat.D32_SFloat,msaaSamples=1,sRGB=false});target.Create();
   command=new CommandBuffer {name="Crossover native timestamp diagnostic"};callback=CrossoverEvent();
   command.IssuePluginEventAndData(callback,0,IntPtr.Zero);Graphics.ExecuteCommandBuffer(command);active=true;deadline=Time.realtimeSinceStartupAsDouble+30;
  } catch(Exception e) {Finish(e);}
 }
 void Stamp(ulong id,int stage) {command.IssuePluginEventAndData(callback,1,new IntPtr(checked((long)(id*4+(uint)stage))));}
 void Update() {
  if(!active)return;
  try {
   int status=CrossoverStatus();if(status<0)throw new InvalidOperationException("Native status "+status);
   if(Time.realtimeSinceStartupAsDouble>deadline)throw new TimeoutException("Native diagnostic timeout");if(status==0)return;
   for(int i=result.resolved;i<result.submitted;i++) {
    int read=CrossoverRead((ulong)i+1,values);if(read<0)throw new InvalidOperationException("Native read "+read);if(read==0)break;
    var s=result.samples[i];if(values[0]!=s.id)throw new InvalidOperationException("Query ID mismatch");
    s.t0=values[1];s.t1=values[2];s.t2=values[3];s.frequency=values[4];s.requiredFence=values[5];s.completedFence=values[6];s.availabilityUnityFrame=Time.frameCount;
    s.gpuCullMs=(s.t1-s.t0)*1000.0/s.frequency;s.gpuDrawMs=(s.t2-s.t1)*1000.0/s.frequency;s.gpuRangeMs=(s.t2-s.t0)*1000.0/s.frequency;result.resolved++;
   }
   if(result.submitted<96) {
    int f=result.submitted;ulong id=(ulong)f+1;if(CrossoverReserve(id)!=1)throw new InvalidOperationException("Ring slot busy; stop without waiting or overwrite");
    var sample=new NativeSample {id=id,submissionUnityFrame=Time.frameCount,availabilityUnityFrame=-1,repetitions=TimingContract.DiagnosticRepetitions(f)};result.samples[f]=sample;
    command.Clear();command.SetRenderTarget(target);command.SetViewport(new Rect(0,0,1280,720));command.ClearRenderTarget(true,true,Color.black,1);
    Stamp(id,0);
    for(int r=0;r<sample.repetitions;r++) {command.SetBufferCounterValue(visible,0);command.DispatchCompute(compute,0,391,1,1);}
    Stamp(id,1);command.CopyCounterValue(visible,args,4);command.DrawProceduralIndirect(Matrix4x4.identity,material,0,MeshTopology.Triangles,args);Stamp(id,2);
    Graphics.ExecuteCommandBuffer(command);result.submitted++;
   } else if(result.resolved==96) {result.completed=true;Finish(null);} else if(++drain>120) throw new TimeoutException("Native drain incomplete");
  } catch(Exception e) {Finish(e);}
 }
 void Finish(Exception e) {active=false;if(e!=null)result.error=e.ToString();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.output)));File.WriteAllText(options.output,JsonUtility.ToJson(result,true));Application.Quit(e==null?0:2);}
 void OnDestroy() {agents?.Release();visible?.Release();args?.Release();command?.Release();if(target!=null)target.Release();if(material!=null)Destroy(material);}
}
}
