"""Package the actual resource-limited ultra-scan run; never claim full completion."""
import argparse
import csv
import json
from pathlib import Path
import shutil
import zipfile
from run_ultra_scan import ARMS, GIB, parse_log, read, sha

ROOT=Path(__file__).resolve().parents[1]
NAME='rtx4090-ultra-scan-20261006'
STAGES=('gate-01','pilot-01','confirmation-01','gate-02','pilot-02','confirmation-02')


def package(session,output):
    analysis=read(session/'analysis-partial-02.json')
    if analysis['complete'] or analysis['processes']!=66 or len(analysis['cells'])!=4:
        raise ValueError('Expected the retained interrupted first campaign.')
    first_plan=read(session/'confirmation-01/plan.json');second_plan=read(session/'confirmation-02/plan.json')
    first_rejected=analysis['rejectedPreflights'][0]
    second_case=second_plan['schedule'][0];second_row=read(session/'confirmation-02'/(second_case['tag']+'.json'))
    second_log=session/'confirmation-02'/(second_case['tag']+'.log')
    if second_row['exitCode']!=5 or sha(second_log)!=second_row['logSha256']:raise ValueError('Second stop receipt differs.')
    second_text=second_log.read_text(encoding='utf-8')
    expected_error='RUNTIME_FAILED Host memory reserve would fall below 4 GiB; stop without allocation.'
    if expected_error not in second_text or 'ultraSample' in second_text or 'ultraValidation' in second_text:
        raise ValueError('Second resource stop was not before allocation/scan.')
    second_events=[json.loads(line[7:]) for line in second_text.splitlines() if line.startswith('HPJSON ')]
    second_stop=next(e for e in second_events if e['kind']=='hostMemory' and e['phase']=='before-allocation')
    if second_stop['availableCommit']>=second_stop['reserve']+second_stop['requiredExtra']:
        raise ValueError('Second native rejection cannot be reproduced from telemetry.')
    for folder,plan in [('measured-source',first_plan),('measured-source-02',second_plan)]:
        if sha(session/folder/'run_ultra_scan.py')!=plan['identity']['ultraRunnerSha256']:raise ValueError('Runner snapshot differs.')
        for name in ['scan-main.cpp','UltraScanSupport.h']:
            if sha(session/folder/name)!=plan['identity']['sourceFiles']['benchmarks/external/native/'+name]:
                raise ValueError('Native snapshot differs.')
    for key,digest in first_plan['identity']['sourceFiles'].items():
        if sha(ROOT/key)!=digest:raise ValueError('Measured kernel/native source changed: '+key)
    output.mkdir(parents=True,exist_ok=True);destination=ROOT/'docs/results/data'/NAME
    destination.mkdir(parents=True,exist_ok=False)
    for name in ['analysis-partial-02.json','analysis-partial-02.csv']:
        shutil.copy2(session/name,destination/name)
    rows=[];stage_rows={}
    for stage in STAGES:
        plan=read(session/stage/'plan.json');current=[]
        for case in plan['schedule']:
            path=session/stage/(case['tag']+'.json')
            if not path.exists():continue
            row=read(path)
            if row.get('exitCode')!=0:continue
            log=session/stage/(case['tag']+'.log')
            if sha(log)!=row['logSha256'] or row['executableSha256']!=plan['identity']['runtimeFiles']['scan/scan.exe']:
                raise ValueError('Changed raw stage receipt: '+str(path))
            parsed=parse_log(log.read_text(encoding='utf-8'),case,plan['device'])
            if any(row[k]!=v for k,v in parsed.items()):raise ValueError('Raw stage evidence differs: '+str(path))
            current.append(row)
            if 'meanMs' in row:
                rows.append(dict(stage=stage,**{k:row[k] for k in ['tag','count','operation','arm','repeat','meanMs','p50Ms','p95Ms','maxMs','withinProcessCv','startedUtc','finishedUtc','logSha256']}))
        stage_rows[stage]=current
    if {stage:len(rs) for stage,rs in stage_rows.items()}!=dict(zip(STAGES,(4,16,66,4,16,0))):
        raise ValueError('Actual stage process inventory differs.')
    with (destination/'all-completed-processes.csv').open('w',newline='',encoding='utf-8') as stream:
        writer=csv.DictWriter(stream,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
    archive=ROOT/'docs/evidence'/(NAME+'.zip')
    if archive.exists():raise FileExistsError('Preserve existing archive.')
    files=[]
    for stage in ['probe-01','probe-02',*STAGES,'measured-source','measured-source-02']:
        files += [p for p in sorted((session/stage).rglob('*')) if p.is_file()]
    for build in ['native-01','native-02']:
        files += [session/build/name for name in ['scan.vcxproj','scan-build.log','preparation.json'] if (session/build/name).is_file()]
    files += [p for p in sorted(session.glob('*.json'))]+[p for p in sorted(session.glob('*.csv'))]
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for path in files:z.write(path,path.relative_to(session).as_posix())
        z.write(destination/'all-completed-processes.csv','all-completed-processes.csv')
    with zipfile.ZipFile(archive) as z:
        if z.testzip() is not None:raise ValueError('Evidence archive CRC failure.')
    manifest=dict(schema='hlslperf.ultra-scan.deliverables.v1',evidenceSha256=sha(archive),evidenceBytes=archive.stat().st_size,
                  sourceBase=first_plan['identity']['sourceCommit'],measurementRunnerSha256=first_plan['identity']['ultraRunnerSha256'],
                  resourceRecoveryRunnerSha256=second_plan['identity']['ultraRunnerSha256'],
                  executableSha256=first_plan['identity']['runtimeFiles']['scan/scan.exe'],
                  confirmationPlanSha256=analysis['planSha256'],sourceFilesVerified=True,archiveCrcPassed=True,
                  archiveEntries=len(files)+1,stageCompletedProcesses={stage:len(rs) for stage,rs in stage_rows.items()},
                  firstCampaignComplete=False,secondCampaignComplete=False,completeCells=4,fullFourSizeConfirmation=False)
    (destination/'deliverables.json').write_text(json.dumps(manifest,indent=2)+'\n')
    lines=['# RTX 4090 超大规模扫描：已完成点与安全停止（2026-10-06）','',
           '**已完成的 6710 万和 1.34 亿元素测量点显示：RTX 4090 原生 D3D12、全 1 uint32 输入下，本地 wave-tiled 的 inclusive/exclusive 完整 GPU 扫描平均耗时比固定 GPUPrefixSums RTS 少约 16–20%。** 两个正式测量阶段均因主机提交余量检查停止，四规模完整确认没有完成。','',
           '2.01 亿和 2.68 亿均通过两次独立预跑，平均值也有利于本地实现，但没有完整六轮正式结果，不能包装为四规模确认成功。没有合并预跑、中止前不完整单元或资源恢复后的测量。','',
           '## 有完整六轮的测量点','',
           '| 元素数 | 类型 | RTS 平均 ms | 本地平均 ms | 配对耗时减少 | 逐点名义 95% 区间 |',
           '|---:|---|---:|---:|---:|---|']
    for c in analysis['cells']:
        a,b=c['processMeanMs']['rts'],c['processMeanMs']['tile-fused'];lo,hi=c['timeReduction95CiPercent']
        lines.append(f"| {c['count']:,} | {c['operation']} | {a:.6f} | {b:.6f} | {c['timeReductionPercent']:.2f}% | [{lo:.2f}%, {hi:.2f}%] |")
    lines += ['', '每个规模/类型/实现有六个独立新进程，每进程 8 次预热、100 次测量。平均 ms 是六个进程均值的算术平均；耗时减少来自配对进程均值比的几何均值，分母为 RTS，因此与两列算术平均值的比可能略有差异。','',
              '区间是逐点、名义 95% Student-t（df=5），未做多重比较校正。内层 100 次操作存在相关性，未当作 100 个独立实验。所有四个完整单元的 RTS/local 比区间下界高于 1；它们支持已测点的平均耗时优势，不能推出整个连续区间。没有剔除耗时尖峰或选取最快进程。','',
              '## 波动和尾部描述','',
              '| 元素数 | 类型 | 实现 | 进程均值 CV | 最大进程内 CV | 六个进程 p50 平均 ms | 六个进程 p95 平均 ms |',
              '|---:|---|---|---:|---:|---:|---:|---:|']
    for c in analysis['cells']:
        for arm in ARMS:
            lines.append(f"| {c['count']:,} | {c['operation']} | {arm} | {100*c['processMeanCv'][arm]:.2f}% | {100*c['maximumWithinProcessCv'][arm]:.2f}% | {c['processP50MeanMs'][arm]:.6f} | {c['processP95MeanMs'][arm]:.6f} |")
    lines += ['', 'p50/p95 是各进程分位数的等权平均，不是合并后的分位数，也未做尾部改善的区间推断。进程内 CV 可达到约 23%；平均耗时优势不等于恒定延迟或 p99 保证。后台应用、时钟与缓存未隔离或固定。','',
              '## 两次预跑：只作探索描述','',
              '| 元素数 | 类型 | 第一次 RTS / 本地 ms | 第二次 RTS / 本地 ms |',
              '|---:|---|---|---|']
    for n in first_plan['counts']:
        for op in ['inclusive','exclusive']:
            pairs=[]
            for stage in ['pilot-01','pilot-02']:
                by={r['arm']:r['meanMs'] for r in stage_rows[stage] if r['count']==n and r['operation']==op}
                pairs.append(f"{by['rts']:.6f} / {by['tile-fused']:.6f}")
            lines.append(f'| {n:,} | {op} | {pairs[0]} | {pairs[1]} |')
    lines += ['', '每个预跑单元只有一次进程均值；没有确认区间，也不并入六轮统计。第一轮 2.01 亿 inclusive 只完成五轮、exclusive 四轮；2.68 亿没有开始正式测量。这些不完整观测的原始日志全部保留。','',
              '## 测量边界和正确性','',
              '- 沿用固定上游 `TimeScan`；GPU 时间包含完整扫描及依赖、本地 lookback 状态 reset，不含编译、分配、输入生成、CPU 提交/等待、查询回读及验证。每个操作独立提交并等待，未堆积成一个巨大 GPU 批次。',
              '- 每次操作前用原版 `InitOne` 重新生成全部输入。两实现直接使用兼容缓冲区，没有 pack/unpack；本轮不测应用整帧。',
              '- 每个成功计时进程在计时前全量 GPU 验证输出，计时后验证最后一次实际计时扫描留下的全部输出，不重新运行扫描；仅回读错误计数，不下载多 GiB 数组到主机。',
              '- 两次 gate 各通过四个独立进程。每 gate 包含 27 个小规模 full32/边界案例、每案例 4 次检查（两种类型），以及对应超大规模检查。两次 pilot 各通过全部 16 个组合。',
              '- 114 个固定第三方源文件字节校验通过；扫描内核、候选参数及 RTS 默认参数没有调优。新增原生入口及安全启动器；本地 wrapper 初始化上游未初始化的 size 字段，不改第三方代码。','',
              '## 两次安全停止','']
    h=first_rejected['telemetry'][0]['host'];required=4*GIB+first_rejected['count']*12+(64<<20)
    lines += [f"第一次在 `{first_rejected['tag']}` 启动前拒绝：可用提交余量 {h['availableCommit']/GIB:.6f} GiB，小于保守分配后保留 4 GiB 所需的 {required/GIB:.6f} GiB；记录没有 PID，进程未启动。此前 66 个进程已执行，四个单元有完整六轮；全轮 `complete=false`。",'',
              '提交余量恢复后，新计划增加每个进程之间 5 秒静置，安全阈值和扫描参数保持不变。gate 和四规模预跑通过，随后尝试重新测量全部四个规模。','',
              f"第二次在第一个 `{second_case['tag']}` 的 native 分配前检查拒绝：可用提交余量 {second_stop['availableCommit']/GIB:.6f} GiB，小于所需 {(second_stop['reserve']+second_stop['requiredExtra'])/GIB:.6f} GiB。子进程正常报告保护性错误并退出 5；没有分配大扫描缓冲区，没有执行 GPU 扫描，没有计时样本。之后停止 GPU 工作，不再重试。",'',
              '- 硬上限 2^28 元素，按 12 字节/元素 +64 MiB 保守预估分配。',
              '- 最低 4 GiB 主机物理/提交余量、8 GiB GPU 空闲；总 GPU 使用上限 12 GiB；80°C 停止。',
              '- 共享 GPU mutex、一个自有子进程、隐藏控制台；第二轮每进程后静置 5 秒。子进程期限 60 秒，单操作 GPU 时间超过 100 ms 停止。',
              '- 任意正确性、设备、资源或来源异常停止整阶段；只允许终止本启动器的子进程。没有修改 TDR、功耗、时钟、页文件或关闭其他应用。安全机制降低风险，不能保证驱动挂起恢复。','']
    res=analysis['resourceSummary']
    lines += [f"第一轮已完成进程中，采样最低物理余量 {res['minimumHostPhysicalBytes']/GIB:.2f} GiB、最低提交余量 {res['minimumHostCommitBytes']/GIB:.2f} GiB，最高 GPU 温度 {res['maximumGpuTemperatureC']}°C；NVIDIA 最大采样显存 {res['maximumSampledGpuUsedBytes']/GIB:.2f} GiB，native 最大 DXGI usage {res['maximumNativeDxgiUsageBytes']/GIB:.2f} GiB。NVIDIA 约每秒采样，短进程可能未覆盖分配峰值，因此不将该采样最大值称为全系统真实峰值；保留 native 分配后 usage 和预算。",'',
              '未观察到设备丢失、进程超时或扫描校验失败；发生的是两次资源保护停止。系统日志检查未见本次窗口中的 Display 4101 / Kernel-Power 41，系统启动时间早于实验。第一次编译选中缺少 C++ 工具链的 Community 后失败，尚未执行 GPU；使用现有 BuildTools v143 构建成功。两次编译日志均保留。','',
              '## 可以支持的表述','',
              '> 在 RTX 4090 原生 D3D12 基准中，本地 wave-tiled 实现在已完成六轮的 6710 万和 1.34 亿元素、全 1 uint32 输入上，对 inclusive/exclusive 完整 GPU 扫描平均耗时比固定 GPUPrefixSums RTS 少约 16–20%。','',
              '不支持所有大规模、所有成熟库、应用整帧加速或确定优势起点的结论。Unity 16M 测试与本轮原生 D3D12 的编译/运行环境不同，不能拼接出已确认的交叉点。历史 2026-09-15 结果使用旧驱动和不同预热，没有合并统计。底层为 GPUPrefixSums 衍生适配，不宣称原创扫描算法。','',
              '## 来源与复现','',
              f"- RTX 4090，驱动 `{analysis['device']['driver']}`（617.14）；本地 cs_6_6 / RTS 默认 cs_6_7；固定 DXC 1.8.2403.18、D3D12 1.613.0、MSVC v143。",
              f"- 测量源基点 `{manifest['sourceBase']}` 加明确 native/runner overlay；exe SHA256 `{manifest['executableSha256']}`。",
              f"- 第一轮 runner SHA256 `{manifest['measurementRunnerSha256']}`；第二轮 `{manifest['resourceRecoveryRunnerSha256']}`；源快照随证据保存。",
              f"- [逐点部分审计](data/{NAME}/analysis-partial-02.json) · [汇总 CSV](data/{NAME}/analysis-partial-02.csv) · [全部成功计时进程 CSV](data/{NAME}/all-completed-processes.csv)。",
              f"- [全部原始日志、两次停止、计划和测量源快照](../evidence/{NAME}.zip)，SHA256 `{manifest['evidenceSha256']}`；zip CRC 通过。",
              '- [安全协议与运行命令](../integration/ULTRA_SCAN_PROTOCOL.md)。CPU 安全/部分审计测试、旧 inclusive 分析回归、来源契约验证通过。','']
    report=ROOT/'docs/results'/(NAME+'.md');report.write_text('\n'.join(lines),encoding='utf-8')
    for path in [report,archive,destination/'analysis-partial-02.json',destination/'analysis-partial-02.csv',destination/'deliverables.json']:
        target=output/path.name
        if target.exists():raise FileExistsError('Preserve delivered file: '+str(target))
        shutil.copy2(path,target)
    exported=output/report.name;text=exported.read_text(encoding='utf-8')
    base='https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/blob/benchmark/fluid-exclusive-scan/docs/'
    text=text.replace('(data/','('+base+'results/data/').replace('(../integration/','('+base+'integration/')
    text=text.replace(f'(../evidence/{NAME}.zip)',f'({(output/archive.name).as_posix()})')
    exported.write_text(text,encoding='utf-8')
    print(json.dumps(manifest,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--session',required=True,type=Path);p.add_argument('--output',required=True,type=Path)
    a=p.parse_args();package(a.session.resolve(),a.output.resolve())
