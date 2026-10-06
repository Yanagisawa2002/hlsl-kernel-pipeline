"""Package the actual audited fluid cohort, including its retained thermal stop."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

from analyze_fluid_frame_confirmation import ARMS, METRICS, audit_stage
from run_ultra_scan import GIB, read, sha

ROOT=Path(__file__).resolve().parents[1]
NAME='fluid-frame-confirmation-20261006'


def package(session,output,reference_plan):
    analysis=read(session/'analysis.json')
    plan,rows=audit_stage(session/'confirmation-01','confirm',partial=not analysis['complete'])
    pilot,pilots=audit_stage(session/'pilot-01','pilot')
    if (analysis['complete'] or analysis['processes']!=20 or analysis['processes']!=len(rows) or len(analysis['cells'])!=2
            or analysis['stopCase']['name']!='d23000-r05-original'
            or analysis['planSha256']!=sha(session/'confirmation-01/plan.json')
            or analysis['sourceIdentity']!=plan['identity'] or pilot['identity']!=plan['identity']
            or plan['referencePlanSha256']!=sha(reference_plan)):
        raise ValueError('Application audit and raw inventory differ.')
    for name,digest in plan['identity']['runnerFilesSha256'].items():
        if sha(session/'measured-source'/name)!=digest:
            raise ValueError('Measured runner dependency changed: '+name)
        if name=='run_fluid_frame_confirmation.py':
            measured=(session/'measured-source'/name).read_text(encoding='utf-8')
            expected=measured.replace("            resources(host,gpu)\n            row['telemetry'].append(dict(utc=now(),phase='running',host=host,gpu=gpu));time.sleep(.5)",
                                      "            # Retain the rejecting point as well as accepted samples on future runs.\n            row['telemetry'].append(dict(utc=now(),phase='running',host=host,gpu=gpu))\n            resources(host,gpu);time.sleep(.5)")
            if (ROOT/'tools'/name).read_text(encoding='utf-8') not in (measured,expected):raise ValueError('Unrelated post-run runner changes.')
        elif sha(ROOT/'tools'/name)!=digest:raise ValueError('Runner dependency changed: '+name)
    destination=ROOT/'docs/results/data'/NAME
    archive=ROOT/'docs/evidence'/(NAME+'.zip');report=ROOT/'docs/results'/(NAME+'.md')
    exports=[output/(NAME+'.md'),output/(NAME+'.zip'),output/(NAME+'-analysis.json'),
             output/(NAME+'-analysis.csv'),output/(NAME+'-deliverables.json')]
    if destination.exists() or archive.exists() or report.exists() or any(p.exists() for p in exports):
        raise FileExistsError('Preserve earlier application deliverables.')
    destination.mkdir(parents=True);output.mkdir(parents=True,exist_ok=True)
    for name in ('analysis.json','analysis.csv'):shutil.copy2(session/name,destination/name)
    fields=['stage','name','particles','arm','repeat','startedUtc','endedUtc','processSeconds',*METRICS]
    process_csv=destination/'process-means.csv'
    with process_csv.open('w',encoding='utf-8',newline='') as stream:
        writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader()
        for stage,observations in [('pilot',pilots),('confirm',rows)]:
            for row in observations:
                writer.writerow(dict(stage=stage,**{k:row['case'][k] for k in ('name','particles','arm','repeat')},
                                     **{k:row['receipt'][k] for k in ('startedUtc','endedUtc','processSeconds')},
                                     **{k:row['summary']['metrics'][k]['meanMs'] for k in METRICS}))
    files={}
    for folder in ('pilot-01','confirmation-01','measured-source'):
        for path in sorted((session/folder).rglob('*')):
            if path.is_file():files[path.relative_to(session).as_posix()]=path
    for name in ('analysis.json','analysis.csv','host-before.json','host-after.json','system-events.json','capture-health.ps1'):
        files[name]=session/name
    files['reference/application-02-plan.json']=reference_plan
    files['process-means.csv']=process_csv
    for name in ('analyze_fluid_frame_confirmation.py','test_fluid_frame_confirmation.py','package_fluid_frame_confirmation.py','run_fluid_frame_confirmation.py'):
        files['analysis-source/'+name]=ROOT/'tools'/name
    hashes={name:sha(path) for name,path in files.items()}
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for name,path in sorted(files.items()):z.write(path,name)
        z.writestr('entry-sha256.json',json.dumps(hashes,indent=2)+'\n')
    with zipfile.ZipFile(archive) as z:
        if z.testzip() is not None or any(hashlib.sha256(z.read(name)).hexdigest()!=digest for name,digest in hashes.items()):
            raise ValueError('Evidence CRC/SHA256 check failed.')
    manifest=dict(schema='hlslperf.fluid-frame.deliverables.v1',complete=analysis['complete'],formalProcesses=len(rows),plannedFormalProcesses=24,
                  failedOwnProcesses=0 if analysis['complete'] else 1,pendingProcesses=len(analysis['pendingCases']),pilotProcesses=4,
                  confirmedCells=sum(c['confirmationComplete'] for c in analysis['cells']),
                  sourceBase=plan['identity']['sourceBase'],measurementRunnerSha256=plan['identity']['runnerFilesSha256']['run_fluid_frame_confirmation.py'],
                  postStopLoggingFixRunnerSha256=sha(ROOT/'tools/run_fluid_frame_confirmation.py'),
                  playerSha256=plan['identity']['playerFilesSha256']['FluidScan.exe'],evidenceSha256=sha(archive),
                  evidenceBytes=archive.stat().st_size,archiveEntries=len(files)+1,archiveCrcAndSha256Passed=True,
                  priorApplicationOrNativeCohortsPooled=False,recording=False)
    (destination/'deliverables.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    practical=[c for c in analysis['cells'] if c['metrics']['wall_frame']['practicalOnePercentSupported']]
    supported=[c for c in analysis['cells'] if c['metrics']['wall_frame']['favorablePointwiseInterval']]
    if practical:lead='部分测量点支持至少 1% 的整帧平均耗时改善，具体范围见下表；不能把这一结果直接归因于扫描。'
    elif supported:lead='部分测量点支持较小的整帧平均耗时改善，但没有测量点满足预先声明的至少 1% 实用收益标准。'
    else:lead='有完整六轮的测量点没有建立整帧平均耗时改善；未完成点不能作正式推断。'
    lines=['# 流体应用整帧独立确认 — RTX 4090（2026-10-06）','',
           (f"**计划 24 个正式进程，完成 {len(rows)} 个；阶段因 80°C 温控保护停止，`complete=false`。** " if not analysis['complete'] else
            '**24 个正式独立进程全部完成，计时关联和正确性检查通过。** ')+lead,'',
           '本轮为新的原版与本地直接缓冲区路径对比；与旧流体四轮测试及原生超大规模扫描测试分别统计。'
           '两种实现实际模拟、渲染水流和泡沫，保持相同粒子数、1920×1080、镜头、质量和物理配置，录像关闭。','',
           '## 主要指标：整帧墙钟时间','',
           '| 水粒子数 | 原版平均 ms | 本地 direct 平均 ms | 配对耗时减少 | 逐点名义 95% 区间 | 至少 1% 收益获支持 |',
           '|---:|---:|---:|---:|---|---|']
    for c in analysis['cells']:
        m=c['metrics']['wall_frame'];ci=m['timeReduction95CiPercent']
        reduction=f"{m['timeReductionPercent']:.3f}%" if ci else '未确认'
        interval=f"[{ci[0]:.3f}%, {ci[1]:.3f}%]" if ci else f"仅 {m['independentRounds']} 轮，未计算区间"
        lines.append(f"| {c['particles']:,} | {m['processMeanMs']['original']:.6f} | {m['processMeanMs'][ARMS[1]]:.6f} | "
                     f"{reduction} | {interval} | {'未确认' if not ci else '是' if m['practicalOnePercentSupported'] else '否'} |")
    lines += ['', '正值表示本地路径耗时减少，负值表示增加。102 万粒子的平均 ms 为六个进程均值的等权平均，'
              '1576 万粒子只有四个进程均值，仅作描述。完整单元的配对百分比由六个 original/direct 进程均值比的几何均值计算。区间用 log 比值 Student-t（df=5），'
              '为逐点名义 95%，未做多重比较校正。相关帧没有当作独立实验；没有剔除任何进程或帧。','',
              '预先声明的实用收益标准是整帧耗时减少区间下界至少 1%；它不是普遍的用户感知阈值。'
              '区间跨越零表示当前测量没有确认方向，不能解释为两种实现完全相等。','',
              '## GPU 分项诊断','',
              '| 水粒子数 | 指标 | 原版平均 ms | 本地 direct 平均 ms | 配对耗时减少 | 逐点名义 95% 区间 |',
              '|---:|---|---:|---:|---:|---|']
    for c in analysis['cells']:
        for metric in METRICS[:-1]:
            m=c['metrics'][metric];ci=m['timeReduction95CiPercent']
            reduction=f"{m['timeReductionPercent']:.3f}%" if ci else '未确认'
            interval=f"[{ci[0]:.3f}%, {ci[1]:.3f}%]" if ci else '四轮描述，无确认区间'
            lines.append(f"| {c['particles']:,} | {metric} | {m['processMeanMs']['original']:.6f} | "
                         f"{m['processMeanMs'][ARMS[1]]:.6f} | {reduction} | {interval} |")
    lines += ['', '各 GPU 指标是嵌套区间，不能相加。扫描时间累加每帧三个 exclusive 调用，包含 reset/必要依赖；'
              '直接路径没有 scan pack/unpack。完整排序仍包含作者的 copyback。GPU simulation 不包含水流/泡沫渲染；'
              'wall frame 包含模拟、渲染、呈现及测试框架。','',
              '| 水粒子数 | 原版扫描 / 整帧预算 | 每帧扫描节省 ms | 扫描节省 / 原版整帧 |',
              '|---:|---:|---:|---:|']
    for c in analysis['cells']:
        lines.append(f"| {c['particles']:,} | {c['originalScanFractionOfWallPercent']:.3f}% | {c['scanMillisecondsSaved']:.6f} | "
                     f"{c['scanSavingsFractionOfOriginalWallPercent']:.3f}% |")
    lines += ['', '预算比例只说明量级。GPU 与 CPU/presentation 可能重叠，不能将该比例当作可加和的因果归因。'
              '原子散射的同键次序不稳定，可能改变后续粒子轨迹与求解器负载；配置相同及位置有限不等于轨迹/图像完全相同。'
              '后台桌面工作和 GPU 时钟未固定，因此即使存在整帧差异，也需区分整体路径表现与扫描本身的贡献。','',
              '## 波动和描述性分位数','',
              '| 水粒子数 | 实现 | 整帧进程均值 CV | 进程 p50 平均 ms | 进程 p95 平均 ms |',
              '|---:|---|---:|---:|---:|']
    for c in analysis['cells']:
        m=c['metrics']['wall_frame']
        for arm in ARMS:lines.append(f"| {c['particles']:,} | {arm} | {100*m['processMeanCv'][arm]:.3f}% | "
                                    f"{m['processP50MeanMs'][arm]:.6f} | {m['processP95MeanMs'][arm]:.6f} |")
    lines += ['', '分位数为各已完成进程分位数的等权平均（102 万六个、1576 万四个），不是合并分位数；没有对尾部收益作区间推断。','',
              '## 执行、正确性与安全','',
              '- 新 pilot 四个进程通过，仅作预检。正式计划每规模/实现六个新进程，共 24 个；每对顺序交替，两路径各先执行三次。'
              '实际完成 102 万粒子全部六轮，以及 1576 万粒子前四轮；没有把预跑或其他阶段补入未完成轮次。',
              '- 120 帧预热 +120 帧测量，固定模拟 2–4 秒窗口，种子 42、dt 1/60、每帧三子步、泡沫容量 1,024,000。',
              '- 每个完成进程通过全输出扫描、排序/排列及空间偏移校验，并在测量结束后检查全部最终位置有限。'
              '每个 source frame 的六个指标完整且正确关联，GPU 查询只在对应 Unity 帧 fence 完成后读取。',
              '- 使用冻结 Unity 6000.3.13f1 Development Player/D3D12，全部 Player 文件与先前成功构建的清单一致。'
              '场景和内核没有重建或调参；新外部启动器的轮次、监控、静置和统计单独冻结。',
              '- 最大 16M 粒子且仅允许已验证的两个负载。256 字节/粒子 +256 MiB 保守预估；'
              '保留 4 GiB 主机物理/提交余量和 8 GiB GPU 空闲，总 GPU 使用上限 12 GiB、温度上限 80°C。',
              '- 共享 GPU mutex，单个自有子进程、间隔 10 秒、自有进程期限 180 秒。'
              'simulation GPU >1000 ms / wall >2000 ms 的完成后检查不能替代每 dispatch watchdog；没有更改系统设置或关闭用户应用。','']
    r=analysis['resourceSummary'];health=read(session/'system-events.json')
    lines += [f"正式阶段最低采样物理余量 {r['minimumHostPhysicalBytes']/GIB:.2f} GiB、提交余量 {r['minimumHostCommitBytes']/GIB:.2f} GiB；"
              f"最高采样温度 {r['maximumGpuTemperatureC']}°C、NVIDIA 显存使用 {r['maximumGpuUsedBytes']/GIB:.2f} GiB。"
              'NVIDIA 约每两秒采样，采样最大值不保证覆盖真实峰值。','',
              f"系统健康查询完成：{health['QueryCompleted']}；运行前后启动时间一致：{health['SameBootAsBefore']}；"
              f"本轮窗口内 Display 4101 / Kernel-Power 41 事件数：{len(health['Entries'])}。没有已完成进程的校验、设备丢失或超时失败。",'',
              '## 安全停止与日志限制','',
              (f"在 `{analysis['stopCase']['name']}` 运行期间，外部监控报告触发 80°C 温控阈值，终止其自有子进程并停止阶段。"
               '该进程未产生最终 run/observations，不能进入计时统计；其后的三个计划进程均未启动。没有降低阈值或继续重试。' if not analysis['complete'] else '本轮没有资源停止。'),'',
              ('旧启动器先检查资源、再追加采样列表，因此触发检查的温度点未被保存；停止进程最后保留的采样为 '
               f"{analysis['stopAudit']['lastRetainedTemperatureC']}°C。原始 receipt/failure 报告温控停止，但该点的精确温度不可从保留采样独立重建。"
               '测量使用的旧源码完整保留，后续启动器已调整为先保存采样再检查；本轮数据和原始源码没有改写。' if not analysis['complete'] and not analysis['stopAudit']['rejectedPointRetained'] else '拒绝点已保留或本轮无拒绝点。'),'',
              '## 结论范围与证据','',
              '该应用使用 exclusive scan，长度最高约 1576 万；它没有模拟原生基准的 2.01 亿/2.68 亿元素。'
              '原生超大规模扫描优势依然是独立结果，不能将其百分比套用到这个应用，或据此宣称更多粒子达到 60 FPS。','',
              f"- [完成单元与中止审计](data/{NAME}/analysis.json) · [分项 CSV](data/{NAME}/analysis.csv) · [逐进程均值](data/{NAME}/process-means.csv)。",
              f"- [所有原始帧观测、日志、资源、计划、来源快照](../evidence/{NAME}.zip)，SHA256 `{manifest['evidenceSha256']}`，CRC 与逐文件 SHA256 均通过。",
              f"- 测量源基点 `{manifest['sourceBase']}` 加新启动器 overlay；Player SHA256 `{manifest['playerSha256']}`；"
              f"runner SHA256 `{manifest['measurementRunnerSha256']}`。",
              f"- NVIDIA 资源采样设备 `{analysis['deviceTelemetry']['adapter']}`，驱动 `{analysis['deviceTelemetry']['driver']}`；整个正式阶段身份一致。",
              '- [本轮预声明协议](../integration/FLUID_FRAME_CONFIRMATION.md) · [旧流体扫描/整帧结果](fluid-scan-scaling-rtx4090-20261005.md) · [原生超大规模结果](rtx4090-ultra-scan-formal-20261006.md)。',
              '- 新安全/六轮推断 CPU 测试、既有帧关联/分析回归通过；Player 构建来自冻结证据，本轮以实际 GPU 执行重新验证。','']
    report.write_text('\n'.join(lines),encoding='utf-8')
    for source,target in zip((report,archive,destination/'analysis.json',destination/'analysis.csv',destination/'deliverables.json'),exports,strict=True):
        shutil.copy2(source,target)
    text=exports[0].read_text(encoding='utf-8')
    base='https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/blob/benchmark/fluid-exclusive-scan/docs/'
    text=text.replace('(data/','('+base+'results/data/').replace('(../integration/','('+base+'integration/')
    for filename in ('fluid-scan-scaling-rtx4090-20261005.md','rtx4090-ultra-scan-formal-20261006.md'):
        text=text.replace('('+filename+')','('+base+'results/'+filename+')')
    text=text.replace(f'(../evidence/{NAME}.zip)',f'({exports[1].as_posix()})')
    exports[0].write_text(text,encoding='utf-8');print(json.dumps(manifest,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--session',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);p.add_argument('--reference-plan',type=Path,required=True)
    a=p.parse_args();package(a.session.resolve(),a.output.resolve(),a.reference_plan.resolve())
