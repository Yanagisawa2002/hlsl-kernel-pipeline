"""Build isolated30s cyclic-state diagnostic source; formal v4 sources/DLL unchanged."""
import argparse,hashlib,json,subprocess
from pathlib import Path
from prepare_draw_drift import patch
ROOT=Path(__file__).resolve().parents[1]
def transform(s):
 s=patch(s,'public int frameIndex, visibleCount,','public int globalFrameIndex,viewId,cycleIndex; public double submissionUtcMs,elapsedSeconds,availabilityUtcMs;\n        public int frameIndex, visibleCount,')
 s=patch(s,'public int schema = 4, protocolVersion = 4','public bool diagnosticOnly=true,eligibleForPerformanceAcceptance=false;\n        public string arm,diagnosticIdentity; public double firstWorkloadTimestamp,diagnosticEndTimestamp,elapsedSeconds,warmupLastSubmissionTimestamp,warmupFencePassedTimestamp,firstMeasuredSubmissionTimestamp; public int framesExecuted,cyclesCompleted;\n        public int schema = 105, protocolVersion = 0')
 s=patch(s,'bool measuring, finished;','bool measuring, finished,diagnosticLastFrame; const int FrameCap=2000000; long firstWorkloadTick;\n        static double UtcMs => (DateTime.UtcNow.Ticks-621355968000000000L)/10000.0;')
 s=patch(s,'options = Options.Parse(Environment.GetCommandLineArgs());','options = Options.Parse(Environment.GetCommandLineArgs());\n                if((options.mode!="cpu" && options.mode!="gpu") || options.agents!=100000 || options.frames!=1000 || options.warmup!=300 || options.timestamps!="on")throw new ArgumentException("State diagnostic contract");\n                options.warmup=0;')
 s=patch(s,'processId = Process.GetCurrentProcess().Id, sourceIdentity = Identity,','arm=options.mode,diagnosticIdentity=Resources.Load<TextAsset>("state-diagnostic-identity").text.Trim(),\n                    processId = Process.GetCurrentProcess().Id, sourceIdentity = Identity,')
 s=patch(s,'result.samples = new Sample[options.frames];','result.mode="gpu-state-stability-diagnostic";result.samples = new Sample[FrameCap];')
 s=patch(s,'for (int f=0;f<options.frames;f++) result.samples[f] = new Sample { frameIndex = f, visibleCount = calibration.views[f].visible };','for (int f=0;f<FrameCap;f++) result.samples[f] = new Sample { frameIndex=f,globalFrameIndex=f,viewId=f%1000,cycleIndex=f/1000,visibleCount=calibration.views[f%1000].visible };')
 s=s.replace('nextFrame == -1','nextFrame == 299')
 s=s.replace('measuredFrame == options.frames - 1','diagnosticLastFrame')
 s=patch(s,'Graphics.ExecuteCommandBuffer(commands);\n            long end', '''sample.submissionUtcMs=UtcMs;
            if(firstWorkloadTick==0) {firstWorkloadTick=Stopwatch.GetTimestamp();result.firstWorkloadTimestamp=sample.submissionUtcMs;}
            sample.elapsedSeconds=(Stopwatch.GetTimestamp()-firstWorkloadTick)/(double)Stopwatch.Frequency;
            if(measuredFrame==299)result.warmupLastSubmissionTimestamp=sample.submissionUtcMs;
            if(measuredFrame==300)result.firstMeasuredSubmissionTimestamp=sample.submissionUtcMs;
            Graphics.ExecuteCommandBuffer(commands);
            long end''')
 start=s.index('                if (nextFrame == 0 && !result.warmupFenceCompleted)')
 stop=s.index('                if (nextFrame >= options.frames)',start)
 s=s[:start]+'''                if(warmupFenceInserted && result.warmupFencePassedTimestamp==0 && warmupFence.passed) {
                    result.warmupFencePassedTimestamp=UtcMs;result.warmupFenceCompleted=true;
                }
'''+s[stop:]
 s=patch(s,'if (nextFrame >= options.frames)','if (diagnosticLastFrame)')
 s=patch(s,'mappedFrames == options.frames','mappedFrames == nextFrame')
 s=patch(s,'int index = nextFrame < 0 ? (nextFrame + options.warmup) % options.frames : nextFrame;', '''if(nextFrame>=FrameCap)throw new InvalidOperationException("Safety frame cap reached before30s");
                diagnosticLastFrame=firstWorkloadTick!=0 && (now-firstWorkloadTick)/(double)Stopwatch.Frequency>=30;
                int index=nextFrame%1000;''')
 s=patch(s,'if(index>=options.frames || index!=mappedFrames)','if(index>=FrameCap || index!=mappedFrames)')
 s=patch(s,'sample.availabilityUnityFrame=Time.frameCount;','sample.availabilityUtcMs=UtcMs;sample.availabilityUnityFrame=Time.frameCount;')
 s=patch(s,'            result.completed = true; Finish(0);','result.framesExecuted=nextFrame;result.cyclesCompleted=nextFrame/1000;result.diagnosticEndTimestamp=result.samples[nextFrame-1].submissionUtcMs;result.elapsedSeconds=result.samples[nextFrame-1].elapsedSeconds;Array.Resize(ref result.samples,nextFrame);\n            result.completed = true; Finish(0);') # Anchor appears only FinishTiming? validate uses same string? guarded below.
 s=patch(s,'result.batchCompletionMs / options.frames','result.batchCompletionMs / nextFrame')
 s=patch(s,'(observed - boundStart) * TickMs / options.frames','(observed - boundStart) * TickMs / nextFrame')
 s=patch(s,'finished = true; measuring = false;', 'finished = true; measuring = false; if(result.samples.Length>nextFrame)Array.Resize(ref result.samples,Math.Max(0,nextFrame));')
 s=patch(s,'string json=JsonUtility.ToJson(result,true);','string json=JsonUtility.ToJson(result,false);')
 return s

def main():
 p=argparse.ArgumentParser();p.add_argument('--project',type=Path,required=True);p.add_argument('--native-plugin',type=Path,required=True);a=p.parse_args()
 if a.project.exists():raise ValueError('fresh isolated project required')
 subprocess.run(['python',str(ROOT/'tools/prepare_crossover_project.py'),'--project',str(a.project),'--native-plugin',str(a.native_plugin)],check=True)
 src=ROOT/'unity/GpuDrivenCrowdBenchmark/CrossoverBenchmark.cs';generated=transform(src.read_text(encoding='utf-8'));target=a.project/'Assets/GpuDrivenCrowdBenchmark/CrossoverBenchmark.cs';target.write_text(generated,encoding='utf-8')
 identity=hashlib.sha256((Path(__file__).read_bytes()+generated.encode('utf-8'))).hexdigest();(a.project/'Assets/Resources/state-diagnostic-identity.txt').write_text(identity)
 receipt={'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'diagnosticIdentity':identity,'generatedSha256':hashlib.sha256(target.read_bytes()).hexdigest(),'originalTimestampDllSha256':hashlib.sha256(a.native_plugin.read_bytes()).hexdigest(),'frameCap':2000000,'durationSeconds':30}
 (a.project/'state-diagnostic-build-input.json').write_text(json.dumps(receipt,indent=2))
if __name__=='__main__':main()
