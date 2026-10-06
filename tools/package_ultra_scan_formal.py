"""Close out the separate, completed two-size ultra-scan confirmation cohort."""
import argparse
import csv
from datetime import datetime
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

from run_ultra_scan import ARMS, GIB, check_resources, parse_log, read, schedule, sha, validate_policy

ROOT = Path(__file__).resolve().parents[1]
NAME = 'rtx4090-ultra-scan-formal-20261006'
STAGES = {'gate-03': ('gate', 4), 'pilot-03': ('pilot', 8), 'confirmation-03': ('confirm', 48)}


def audit_stage(folder, expected_stage, expected_count, identity, device):
    plan = read(folder/'plan.json')
    completion = read(folder/'completion.json')
    if (plan['stage'] != expected_stage or plan['identity'] != identity or plan['device'] != device
            or plan['counts'] != [201326592, 268435456] or plan['schedule'] != schedule(expected_stage, plan['counts'])
            or not completion['complete'] or completion['processes'] != expected_count
            or completion['planSha256'] != sha(folder/'plan.json') or (folder/'failure.json').exists()):
        raise ValueError('Stage identity/completion differs: '+str(folder))
    validate_policy(plan['policy'])
    if plan['policy']['interProcessRestSeconds'] != 10:
        raise ValueError('This cohort declared ten seconds of rest.')
    rows = []
    previous = None
    for case in plan['schedule']:
        row = read(folder/(case['tag']+'.json'))
        log = folder/(case['tag']+'.log')
        if (any(row[k] != v for k, v in case.items()) or row['exitCode'] != 0 or row.get('stopReason')
                or row.get('preflightRejected') or row['executableSha256'] != identity['runtimeFiles']['scan/scan.exe']
                or sha(log) != row['logSha256']):
            raise ValueError('Failed/changed stage receipt: '+case['tag'])
        parsed = parse_log(log.read_text(encoding='utf-8'), case, device)
        if any(row[k] != v for k, v in parsed.items()):
            raise ValueError('Receipt differs from raw log: '+case['tag'])
        start, finish = map(datetime.fromisoformat, (row['startedUtc'], row['finishedUtc']))
        if finish <= start or previous and (start-previous).total_seconds()+.1 < 10:
            raise ValueError('Stage process interval differs: '+case['tag'])
        previous = finish
        if row['telemetry'][0]['phase'] != 'before' or row['telemetry'][-1]['phase'] != 'after':
            raise ValueError('Resource telemetry incomplete.')
        for point in row['telemetry']:
            check_resources(point['host'], point['gpu'], case['count'] if point['phase'] == 'before' else 0)
        rows.append(row)
    if len(rows) != expected_count:
        raise ValueError('Unexpected process inventory.')
    return rows


def package(session, output):
    analysis = read(session/'analysis-formal-03.json')
    plan = read(session/'confirmation-03/plan.json')
    if (not analysis['complete'] or analysis['processes'] != 48 or len(analysis['cells']) != 4
            or analysis['planSha256'] != sha(session/'confirmation-03/plan.json')
            or analysis['sourceIdentity'] != plan['identity'] or analysis['incompleteCells']
            or analysis['rejectedPreflights'] or analysis['pendingCases']):
        raise ValueError('Expected a fully audited, separate 48-process confirmation.')
    fixed = plan['identity']
    health = read(session/'system-events-formal-03.json')
    after = read(session/'host-after-formal-03.json')
    finished = read(session/'confirmation-03/completion.json')['finishedUtc']
    if datetime.fromisoformat(health['EndUtc']) < datetime.fromisoformat(finished):
        raise ValueError('System health capture predates confirmation completion.')
    stage_rows = {stage: audit_stage(session/stage, mode, count, fixed, plan['device'])
                  for stage, (mode, count) in STAGES.items()}
    for name in ('gate', 'pilot'):
        folder = session/(name+'-03')
        expected = dict(planSha256=sha(folder/'plan.json'), completionSha256=sha(folder/'completion.json'))
        if plan['prerequisites'][name] != expected:
            raise ValueError('Prerequisite receipt changed: '+name)
    if sha(session/'measured-source-03/run_ultra_scan.py') != fixed['ultraRunnerSha256']:
        raise ValueError('Measurement runner snapshot changed.')
    for name in ('UltraScanSupport.h', 'scan-main.cpp'):
        if sha(session/'measured-source-03'/name) != fixed['sourceFiles']['benchmarks/external/native/'+name]:
            raise ValueError('Native snapshot changed: '+name)
    sources = dict(fixed['sourceFiles'])
    sources['tools/run_ultra_scan.py'] = fixed['ultraRunnerSha256']
    sources['tools/run_inclusive_scan.py'] = fixed['runnerSha256']
    if any(sha(ROOT/name) != digest for name, digest in sources.items()):
        raise ValueError('Current source differs from measured bytes.')
    destination = ROOT/'docs/results/data'/NAME
    if destination.exists():
        raise FileExistsError('Preserve existing cohort deliverables.')
    destination.mkdir(parents=True)
    output.mkdir(parents=True, exist_ok=True)
    for name in ('analysis-formal-03.json', 'analysis-formal-03.csv'):
        shutil.copy2(session/name, destination/name)
    fields = ['stage', 'tag', 'count', 'operation', 'arm', 'repeat', 'meanMs', 'p50Ms', 'p95Ms', 'maxMs',
              'withinProcessCv', 'startedUtc', 'finishedUtc', 'logSha256']
    process_csv = destination/'all-completed-processes.csv'
    with process_csv.open('w', newline='', encoding='utf-8') as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for stage, rows in stage_rows.items():
            for row in rows:
                if 'meanMs' in row:
                    writer.writerow(dict(stage=stage, **{k: row[k] for k in fields if k != 'stage'}))
    archive = ROOT/'docs/evidence'/(NAME+'.zip')
    if archive.exists():
        raise FileExistsError('Preserve existing evidence archive.')
    files = {}
    for stage in ('probe-03', *STAGES, 'measured-source-03'):
        for path in sorted((session/stage).rglob('*')):
            if path.is_file():
                files[path.relative_to(session).as_posix()] = path
    for name in ('analysis-formal-03.json', 'analysis-formal-03.csv', 'host-before-formal-03.json',
                 'host-after-formal-03.json', 'system-events-formal-03.json', 'capture-host-formal-03.ps1'):
        path = session/name
        if not path.is_file():
            raise FileNotFoundError(path)
        files[name] = path
    for name in ('host-after-formal-03-initial.json', 'system-events-formal-03-initial.json', 'health-audit-correction.json'):
        if (session/name).exists():
            files[name] = session/name
    for name in ('preparation.json', 'scan.vcxproj', 'scan-build.log'):
        files['native-02/'+name] = session/'native-02'/name
    files['all-completed-processes.csv'] = process_csv
    for name in sources:
        files['measured-repository/'+name] = ROOT/name
    for name in ('third_party/upstream-lock.json', 'tools/package_ultra_scan_formal.py'):
        files['measured-repository/'+name] = ROOT/name
    digests = {name: sha(path) for name, path in sorted(files.items())}
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for name, path in sorted(files.items()):
            zipped.write(path, name)
        zipped.writestr('entry-sha256.json', json.dumps(digests, indent=2)+'\n')
    with zipfile.ZipFile(archive) as zipped:
        if zipped.testzip() is not None or len(zipped.namelist()) != len(files)+1:
            raise ValueError('Evidence archive verification failed.')
        if any(hashlib.sha256(zipped.read(name)).hexdigest() != digest for name, digest in digests.items()):
            raise ValueError('Archive entry SHA256 differs from the measured source/evidence.')
    manifest = dict(schema='hlslperf.ultra-scan.deliverables.v2', cohortComplete=True,
                    counts=plan['counts'], confirmationProcesses=48, completeCells=4,
                    stageCompletedProcesses={k: len(v) for k, v in stage_rows.items()},
                    sourceBase=fixed['sourceCommit'], measurementRunnerSha256=fixed['ultraRunnerSha256'],
                    executableSha256=fixed['runtimeFiles']['scan/scan.exe'], confirmationPlanSha256=analysis['planSha256'],
                    sourceFilesVerified=True, archiveCrcPassed=True, archiveEntries=len(files)+1,
                    evidenceSha256=sha(archive), evidenceBytes=archive.stat().st_size,
                    priorCohortsPooled=False, interProcessRestSeconds=10)
    (destination/'deliverables.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    favorable = all(c['favorablePointwiseInterval'] for c in analysis['cells'])
    reductions = [c['timeReductionPercent'] for c in analysis['cells']]
    lead = (f"本轮四个测量单元均支持本地实现的平均耗时优势：比固定 GPUPrefixSums RTS 少 {min(reductions):.2f}–{max(reductions):.2f}%。"
            if favorable else '本轮结果如下；没有把不利或不确定的测量单元排除。')
    lines = ['# RTX 4090 超大规模扫描：2.01 亿和 2.68 亿正式确认（2026-10-06）', '',
             '**48 个正式独立进程全部完成，正确性和资源检查通过。** '+lead, '',
             '范围为 RTX 4090、原生 D3D12、全 1 uint32 输入、inclusive/exclusive 完整 GPU 扫描。'
             '这是两个此前未完成规模的新确认阶段；旧中止阶段及预跑没有并入统计。', '',
             '## 六轮正式结果', '',
             '| 元素数 | 类型 | RTS 平均 ms | 本地平均 ms | 配对耗时减少 | 逐点名义 95% 区间 |',
             '|---:|---|---:|---:|---:|---|']
    for c in analysis['cells']:
        lo, hi = c['timeReduction95CiPercent']
        lines.append(f"| {c['count']:,} | {c['operation']} | {c['processMeanMs']['rts']:.6f} | "
                     f"{c['processMeanMs']['tile-fused']:.6f} | {c['timeReductionPercent']:.2f}% | [{lo:.2f}%, {hi:.2f}%] |")
    lines += ['', '每规模/语义/实现六个新进程，各进程 8 次预热、100 次测量。每轮两实现的顺序交替，'
              'inclusive/exclusive 顺序也交替；进程之间至少静置 10 秒。没有剔除样本或选择最快进程。', '',
              '平均 ms 为六个进程均值的算术平均；耗时减少由六个配对 RTS/local 进程均值比的几何均值计算，'
              '分母为 RTS。逐点区间用 log 比值的 Student-t（df=5），未做多重比较校正。'
              '内层 100 次相关操作没有当作独立重复；这些区间不能推出连续规模区间或所有输入的优势。', '',
              '## 波动描述', '',
              '| 元素数 | 类型 | 实现 | 进程均值 CV | 最大进程内 CV | 进程 p50 平均 ms | 进程 p95 平均 ms |',
              '|---:|---|---|---:|---:|---:|---:|']
    for c in analysis['cells']:
        for arm in ARMS:
            lines.append(f"| {c['count']:,} | {c['operation']} | {arm} | {100*c['processMeanCv'][arm]:.2f}% | "
                         f"{100*c['maximumWithinProcessCv'][arm]:.2f}% | {c['processP50MeanMs'][arm]:.6f} | {c['processP95MeanMs'][arm]:.6f} |")
    lines += ['', 'p50/p95 是六个进程各自分位数的等权平均，不是合并样本的分位数。'
              '没有推断尾部改善或恒定延迟；后台应用、GPU 时钟和缓存没有固定。', '',
              '## 测量边界与正确性', '',
              '- 沿用固定上游 `TimeScan`，测完整 GPU 扫描及依赖，包含本地 lookback 状态 reset。'
              '输入生成、编译、分配、验证、CPU 提交/等待、查询回读均在 GPU 时间区间外。',
              '- 每次操作前以固定上游 `InitOne` 重建输入；每次扫描单独提交并等待。'
              '两实现直接使用兼容缓冲区，没有 pack/unpack 或复制桥接。',
              '- 新 gate 的四个进程通过小规模 27 个 full32/边界案例（每案例四次）和超大规模全量输出检查。'
              '新 pilot 的八个组合全部通过；pilot 只作预检，不进入正式统计。',
              '- 48 个正式进程各自在计时前全量验证，计时后验证最后一次实际计时输出，不重跑扫描。'
              '大数组在 GPU 上检查，仅回读错误计数。',
              '- 114 个固定第三方文件字节校验通过。扫描内核、RTS 默认参数、本地候选参数和二进制均保持原样；'
              '新启动器只允许增加静置时间，原有安全阈值不变。', '', '## 资源与完成状态', '']
    r = analysis['resourceSummary']
    health_text = ('运行前后系统启动时间一致，本轮窗口内系统日志未见 Display 4101 / Kernel-Power 41 事件。'
                   if health['QueryCompleted'] and health['SameBootAsBefore'] and after['SameBootAsBefore'] and not health['Entries']
                   else f"系统健康查询完成状态为 {health['QueryCompleted']}，启动时间一致状态为 {after['SameBootAsBefore']}，"
                        f"记录 {len(health['Entries'])} 个 Display 4101 / Kernel-Power 41 事件；详见原始记录。")
    lines += [f"正式阶段最低采样物理余量 {r['minimumHostPhysicalBytes']/GIB:.2f} GiB、提交余量 "
              f"{r['minimumHostCommitBytes']/GIB:.2f} GiB；最高采样温度 {r['maximumGpuTemperatureC']}°C。"
              f"NVIDIA 最大采样显存使用 {r['maximumSampledGpuUsedBytes']/GIB:.2f} GiB，"
              f"native 最大 DXGI usage {r['maximumNativeDxgiUsageBytes']/GIB:.2f} GiB。", '',
              'NVIDIA 约每秒采样，短进程可能未覆盖分配峰值；这些采样最大值不等于全系统真实峰值。'
              '保留 native 分配后 usage/预算、主机分配前检查和运行期间资源采样。', '',
              '- 元素上限 2^28，按 12 字节/元素 +64 MiB 保守预估分配；保留 4 GiB 主机物理/提交余量及 8 GiB GPU 空闲。',
              '- 总 GPU 使用上限 12 GiB、温度阈值 80°C、自有子进程期限 60 秒、单次 GPU 时间上限 100 ms。',
              '- 共享 GPU mutex，每次仅一个自有进程；没有录像，没有改变 TDR、时钟、功耗、页文件或关闭用户应用。',
              '- 本轮 gate/pilot/confirmation 均完整结束，没有资源拒绝、设备丢失、超时或扫描校验失败。'
              '系统事件检查和运行前后启动时间另存原始证据；资源保护不能保证驱动挂起时的恢复。', '',
              health_text, '',
              '旧的两次资源停止和 6710 万/1.34 亿六轮结果仍在[先前报告](rtx4090-ultra-scan-20261006.md)。'
              '本轮在系统已重新启动、内存余量恢复后重新探测设备 LUID；没有将不同阶段拼成一次完整 96 进程测试。', '',
              '## 可支持的表述', '',
              '> 在 RTX 4090 原生 D3D12、全 1 uint32 输入的已测超大规模点上，本地 wave-tiled 实现相比固定 RTS 基线具有平均扫描耗时优势。', '',
              '这不支持所有大规模、全部成熟库或应用整帧提速；Unity 流体的整帧收益尚未成立。'
              '不能与 Unity 16M 或旧驱动历史数据拼接确定优势起点。底层为 GPUPrefixSums 衍生适配，不宣称原创扫描算法。', '',
              '## 来源与复现', '',
              f"- RTX 4090，驱动 `{analysis['device']['driver']}`（617.14），设备 LUID `{analysis['device']['luid']}`；"
              '本地 cs_6_6 / RTS 默认 cs_6_7；DXC 1.8.2403.18、D3D12 1.613.0、MSVC v143。',
              f"- 测量源基点 `{manifest['sourceBase']}` 加启动器 overlay；runner SHA256 `{manifest['measurementRunnerSha256']}`。",
              f"- 复用已验证原生二进制，SHA256 `{manifest['executableSha256']}`；原始 build/preparation 记录、本地测量来源字节和上游锁定清单随 archive 保存。",
              f"- [逐点审计](data/{NAME}/analysis-formal-03.json) · [汇总 CSV](data/{NAME}/analysis-formal-03.csv) · [全部成功计时进程 CSV](data/{NAME}/all-completed-processes.csv)。",
              f"- [原始计划、日志、资源和来源快照](../evidence/{NAME}.zip)，SHA256 `{manifest['evidenceSha256']}`；"
              'zip CRC 通过，archive 内含逐文件 SHA256 清单。',
              '- [安全协议及两规模复现参数](../integration/ULTRA_SCAN_PROTOCOL.md)。新启动器安全/审计回归和既有来源契约验证通过。', '']
    report = ROOT/'docs/results'/(NAME+'.md')
    if report.exists():
        raise FileExistsError('Preserve existing report.')
    report.write_text('\n'.join(lines), encoding='utf-8')
    for path in (report, archive, destination/'analysis-formal-03.json', destination/'analysis-formal-03.csv', destination/'deliverables.json'):
        target = output/((NAME+'-deliverables.json') if path.name == 'deliverables.json' else path.name)
        if target.exists():
            raise FileExistsError('Preserve existing exported deliverable.')
        shutil.copy2(path, target)
    exported = output/report.name
    text = exported.read_text(encoding='utf-8')
    base = 'https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/blob/benchmark/fluid-exclusive-scan/docs/'
    text = text.replace('(data/', '('+base+'results/data/').replace('(../integration/', '('+base+'integration/')
    text = text.replace('(rtx4090-ultra-scan-20261006.md)', '('+base+'results/rtx4090-ultra-scan-20261006.md)')
    text = text.replace(f'(../evidence/{NAME}.zip)', '('+(output/archive.name).as_posix()+')')
    exported.write_text(text, encoding='utf-8')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--session', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    package(args.session.resolve(), args.output.resolve())
