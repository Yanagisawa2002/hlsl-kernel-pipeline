# Run with qrenderdoc --python via a hidden shell process. This is capture-only;
# the extended warmup avoids capturing Unity's splash screen. Never use its timings.
import renderdoc as rd, os, json, time, traceback
out=os.environ['CROSSOVER_CAPTURE_OUTPUT'];os.makedirs(out,exist_ok=True)
try:
 arm=os.environ.get('CROSSOVER_CAPTURE_ARM','gpu')
 exe=os.environ['CROSSOVER_CAPTURE_PLAYER']
 args='-force-d3d12 -screen-fullscreen 0 -logFile '+out+'/'+arm+'.log --mode '+arm+' --agents 100000 --density 0.25 --seed 69501203 --frames 96 --warmup-frames '+('50000' if arm=='gpu' else '2000')+' --timing-api native --timestamps on --calibration '+os.environ['CROSSOVER_CAPTURE_CALIBRATION']+' --output '+out+'/'+arm+'.json'
 opts=rd.CaptureOptions()
 launched=rd.ExecuteAndInject(exe,os.path.dirname(exe),args,[],out+'/'+arm,opts,False)
 target=rd.CreateTargetControl('',launched.ident,'Crossover placement',False)
 if target is None:raise RuntimeError('No target')
 start=time.time();triggered=False
 captures=[];deadline=time.time()+60
 while target.Connected() and time.time()<deadline:
  if not triggered and time.time()-start>5:
   target.TriggerCapture(1);triggered=True
  msg=target.ReceiveMessage(None)
  if msg.type==rd.TargetControlMessageType.NewCapture:captures.append(msg.newCapture.path)
 target.Shutdown()
 json.dump({'args':args,'captures':captures},open(out+'/'+arm+'-capture.json','w'),indent=2)
 for capture in captures:
  cap=rd.OpenCaptureFile();cap.OpenFile(capture,'',None)
  status,controller=cap.OpenCapture(rd.ReplayOptions(),None)
  if controller is None:raise RuntimeError(str(status))
  structured=controller.GetStructuredFile()
  chunks=[]
  def child(c):
   return {'name':c.name,'type':str(c.type.basetype),'value':str(c.AsString()) if c.type.basetype==rd.SDBasic.String else str(c.AsInt()) if c.type.basetype in (rd.SDBasic.UnsignedInteger,rd.SDBasic.SignedInteger) else '', 'children':[child(c.GetChild(i)) for i in range(c.NumChildren())]}
  for index,chunk in enumerate(structured.chunks):
   if any(x in chunk.name for x in ('EndQuery','ResolveQueryData','Dispatch','CopyBufferRegion','ExecuteIndirect','DrawInstanced')):chunks.append({'index':index,'name':chunk.name,'children':[child(chunk.GetChild(i)) for i in range(chunk.NumChildren())]})
  json.dump(chunks,open(out+'/'+arm+'-commands.json','w'),indent=2)
  controller.Shutdown();cap.Shutdown()
except Exception:
 open(out+'/'+os.environ.get('CROSSOVER_CAPTURE_ARM','gpu')+'-error.txt','w').write(traceback.format_exc())
os._exit(0)
