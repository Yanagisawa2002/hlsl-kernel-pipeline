"""Render exportable scan/application figures from validated cohort JSON files."""
import argparse
import csv
import json
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter

ARMS = ('original','hlsl-wave-tiled','gpuprefixsums-rts','hlsl-wave-tiled-direct')
LABELS = ('Original Blelloch','Local + bridges','External RTS + bridges','Local direct buffers')
COLORS = ('#56616d','#b44b42','#8e67ab','#087f8c')


def count_label(value, _=None):
    return f'{value/1e6:.3g}M' if value >= 1e6 else f'{value/1e3:.3g}k'


def axes_style(ax, counts, ylabel):
    ax.set_xscale('log',base=2)
    ticks = counts if len(counts) <= 7 else [counts[i] for i in (0,2,5,7,9,10)]
    ax.set_xticks(ticks)
    ax.xaxis.set_major_formatter(FuncFormatter(count_label))
    ax.set_xlabel('Scan length (uint32 elements)')
    ax.set_ylabel(ylabel)
    ax.grid(True,which='major',alpha=.18)
    ax.spines[['top','right']].set_visible(False)


def save(fig, root, name, footer):
    fig.text(.04,.015,footer,fontsize=9,color='#56616d')
    fig.savefig(root/(name+'.png'),dpi=180,facecolor='white')
    fig.savefig(root/(name+'.svg'),metadata={'Date':None},facecolor='white')
    plt.close(fig)


def scan_figures(sweep, root):
    lengths = sorted(map(int,sweep['lengths']))
    fig, axes = plt.subplots(1,2,figsize=(12,5.4))
    for ax,metric,title in zip(axes,('core','complete'),('Core: reset + full exclusive scan','Complete scan: includes any bridges')):
        for arm,label,color in zip(ARMS,LABELS,COLORS):
            values = [1000*sweep['lengths'][str(n)][arm][metric]['meanOfRunMeanMs'] for n in lengths]
            ax.plot(lengths,values,'o-',label=label,color=color,markersize=4,linewidth=1.8)
        axes_style(ax,lengths,'Microseconds per scan operation')
        ax.set_yscale('log')
        ax.set_title(title,fontsize=12)
    axes[0].legend(fontsize=9,loc='upper left')
    fig.suptitle('RTX 4090 / Unity D3D12: scale and integration cost',fontsize=16,fontweight='bold')
    fig.subplots_adjust(left=.07,right=.985,top=.83,bottom=.18,wspace=.26)
    save(fig,root,'fluid-scan-scaling','4 fresh processes / arm; equal-weight process means. Regenerated full32 inputs; 32 scans/frame. Sequential hot workload.')

    fig,axes = plt.subplots(1,2,figsize=(12,5.4))
    n = lengths[-1]; cell=sweep['lengths'][str(n)]
    core = [1000*cell[a]['core']['meanOfRunMeanMs'] for a in ARMS]
    outside = [1000*cell[a]['adaptation']['meanOfRunMeanMs'] for a in ARMS]
    axes[0].bar(range(4),core,color=COLORS,label='Core (including reset)')
    axes[0].bar(range(4),outside,bottom=core,color=COLORS,hatch='///',alpha=.35,label='Outside core: paired complete - core')
    axes[0].set_xticks(range(4),('Original','Local\n+ bridges','External RTS\n+ bridges','Local\ndirect'))
    axes[0].set_ylabel('Microseconds per scan operation')
    axes[0].set_title(f'Additive timing breakdown at {n:,} elements',fontsize=12)
    axes[0].legend(fontsize=9)
    axes[0].spines[['top','right']].set_visible(False)
    axes[0].grid(axis='y',alpha=.18)
    for i,(a,b) in enumerate(zip(core,outside)):
        axes[0].text(i,a+b+7,f'{a+b:.1f}',ha='center',fontsize=10)
    axes[0].set_ylim(0,max(a+b for a,b in zip(core,outside))*1.2)
    ratios = [sweep['lengths'][str(n)]['hlsl-wave-tiled-direct']['relativeToOriginal']['complete'] for n in lengths]
    values = [r['baselineOverCandidate'] for r in ratios]
    errors = [[v-r['pointwise95Interval'][0] for v,r in zip(values,ratios)],
              [r['pointwise95Interval'][1]-v for v,r in zip(values,ratios)]]
    axes[1].errorbar(lengths,values,yerr=errors,fmt='o-',color=COLORS[3],capsize=3,markersize=4)
    axes[1].axhline(1,color=COLORS[0],linestyle='--',linewidth=1)
    axes_style(axes[1],lengths,'Original / direct complete-scan time')
    axes[1].set_title('Direct versus original: paired process means',fontsize=12)
    axes[1].text(.02,.96,'Above 1 = direct faster',transform=axes[1].transAxes,va='top',fontsize=9)
    fig.suptitle('Removing scan copies reveals a size-dependent advantage',fontsize=16,fontweight='bold')
    fig.subplots_adjust(left=.07,right=.985,top=.83,bottom=.18,wspace=.26)
    save(fig,root,'fluid-scan-integration-cost','4 paired process repeats; nominal pointwise 95% Student-t log-ratio intervals (df=3), unadjusted across lengths.')
    with (root/'scan-scaling.csv').open('x',encoding='utf-8',newline='') as f:
        out=csv.writer(f);out.writerow(['length','arm','core_mean_ms','complete_mean_ms','paired_outside_core_mean_ms','core_p50_ms','complete_p50_ms'])
        for n in lengths:
            for arm in ARMS:
                c=sweep['lengths'][str(n)][arm]
                out.writerow([n,arm,*[c[m]['meanOfRunMeanMs'] for m in ('core','complete','adaptation')],c['core']['medianOfRunP50Ms'],c['complete']['medianOfRunP50Ms']])


def application_figures(application, root):
    counts = sorted(map(int,application['particleCounts']))
    fig,axes = plt.subplots(1,2,figsize=(12,5.4))
    for ax,metric,title in zip(axes,('scan_complete','wall_frame'),('Complete scan: three calls / simulation frame','Whole frame: solver + rendering + harness')):
        for arm,label,color in zip(ARMS,LABELS,COLORS):
            values=[application['particleCounts'][str(n)]['arms'][arm]['metrics'][metric]['meanOfRunMeanMs'] for n in counts]
            ax.plot(counts,values,'o-',label=label,color=color,markersize=4,linewidth=1.8)
        axes_style(ax,counts,'Milliseconds per simulation frame')
        ax.set_xlabel('Actual water particle count')
        ax.set_yscale('log')
        ax.set_title(title,fontsize=12)
    axes[0].legend(fontsize=9,loc='upper left')
    axes[1].axhline(1000/60,color='#808080',linestyle='--',linewidth=1)
    axes[1].text(counts[0],1000/60*1.08,'16.67 ms reference',fontsize=9,color='#666666')
    fig.suptitle('RTX 4090 fluid: scan savings versus application scale',fontsize=16,fontweight='bold')
    fig.subplots_adjust(left=.07,right=.985,top=.83,bottom=.19,wspace=.26)
    save(fig,root,'fluid-application-scaling','4 fresh processes / arm / size; means. 1080p, 3 substeps, fixed physics, simulated 2-4 s; density changes physical load. No recording.')
    fig,axes=plt.subplots(1,2,figsize=(12,5.4))
    for arm,label,color in zip(ARMS[1:],LABELS[1:],COLORS[1:]):
        cells=[application['particleCounts'][str(n)]['arms'][arm]['relativeToOriginal']['wall_frame'] for n in counts]
        values=[c['baselineOverCandidate'] for c in cells]
        errors=[[v-c['pointwise95Interval'][0] for v,c in zip(values,cells)],
                [c['pointwise95Interval'][1]-v for v,c in zip(values,cells)]]
        axes[0].errorbar(counts,values,yerr=errors,fmt='o-',label=label,color=color,capsize=3,markersize=4)
    axes_style(axes[0],counts,'Original / candidate whole-frame time')
    axes[0].set_xlabel('Actual water particle count')
    axes[0].axhline(1,color=COLORS[0],linestyle='--',linewidth=1)
    axes[0].set_title('Whole-frame paired process-mean ratios',fontsize=12)
    axes[0].legend(fontsize=9)
    axes[0].text(.02,.96,'Above 1 = candidate faster',transform=axes[0].transAxes,va='top',fontsize=9)
    fractions=[application['particleCounts'][str(n)]['arms']['original']['scanFractionOfWallPercent'] for n in counts]
    axes[1].bar(range(len(counts)),fractions,color=COLORS[0])
    axes[1].set_xticks(range(len(counts)),[count_label(n) for n in counts])
    axes[1].set_xlabel('Actual water particle count')
    axes[1].set_ylabel('Original complete-scan / wall mean (%)')
    axes[1].set_title('Baseline scan occupies a small frame budget',fontsize=12)
    axes[1].spines[['top','right']].set_visible(False)
    axes[1].grid(axis='y',alpha=.18)
    for i,fraction in enumerate(fractions):axes[1].text(i,fraction+.015,f'{fraction:.2f}%',ha='center',fontsize=9)
    axes[1].set_ylim(0,max(fractions)*1.2)
    fig.suptitle('Application benefit and the baseline scan budget',fontsize=16,fontweight='bold')
    fig.subplots_adjust(left=.07,right=.985,top=.83,bottom=.19,wspace=.26)
    save(fig,root,'fluid-application-uncertainty','4 process pairs, pointwise 95% log-ratio intervals (df=3), unadjusted. Frame changes also include trajectory/background variation; no causal attribution.')
    with (root/'application-scaling.csv').open('x',encoding='utf-8',newline='') as f:
        out=csv.writer(f);out.writerow(['particles','spawn_density','arm','metric','mean_ms','median_run_p50_ms','median_run_p95_ms'])
        for n in counts:
            row=application['particleCounts'][str(n)]
            for arm in ARMS:
                for metric,c in row['arms'][arm]['metrics'].items():
                    out.writerow([n,row['spawnDensity'],arm,metric,c['meanOfRunMeanMs'],c['medianOfRunP50Ms'],c['medianOfRunP95Ms']])


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--sweep',required=True,type=Path)
    p.add_argument('--application',type=Path)
    p.add_argument('--output',required=True,type=Path)
    a=p.parse_args()
    sweep=json.loads(a.sweep.read_text(encoding='utf-8'))
    if sweep['schema'] != 'hlslperf.fluid-scan.sweep-cohort.v1':raise ValueError('Wrong scan cohort schema')
    a.output.mkdir(parents=True,exist_ok=False)
    plt.rcParams.update({'font.family':'DejaVu Sans','font.size':10,'svg.hashsalt':'fluid-scaling-20261005'})
    scan_figures(sweep,a.output)
    if a.application:
        application=json.loads(a.application.read_text(encoding='utf-8'))
        if application['schema'] != 'hlslperf.fluid-scan.application-scaling.v1':raise ValueError('Wrong application cohort schema')
        application_figures(application,a.output)


if __name__ == '__main__':main()
