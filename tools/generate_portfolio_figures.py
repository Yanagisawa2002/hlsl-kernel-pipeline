"""Generate static portfolio figures from docs/data only; --check verifies frozen sources."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

ROOT = Path(__file__).resolve().parents[1]
SLUGS = ("rtx4090-inclusive-scan", "crowd-complete-lifecycle")


def check(data):
    source = json.loads((ROOT / data["source_report"]).read_text(encoding="utf-8-sig"))
    assert (ROOT / data["source_result_document"]).is_file()
    for row in data["rows"]:
        value = source
        for part in row["source_pointer"].strip("/").split("/"):
            value = value[part]
        assert round(value, data["decimals"]) == row["value"], row["id"]


def render(slug, data):
    plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 11,
                         "svg.hashsalt": "hlsl-portfolio-v1", "svg.fonttype": "path"})
    fig, ax = plt.subplots(figsize=(12, 6.4), dpi=120)
    fig.patch.set_facecolor("#fafbfc")
    ax.set_facecolor("#fafbfc")
    fig.subplots_adjust(left=.245, right=.91, bottom=.29, top=.73)
    rows = data["rows"]
    ys = [i + (.4 if len(rows) == 5 and i >= 3 else 0) for i in range(len(rows))]
    colors = ["#245b92" if r["highlight"] else "#8193a5" if r["group"] == "GPU" else "#bdc7d0" for r in rows]
    ax.barh(ys, [r["value"] for r in rows], height=.57, color=colors)
    ax.set_yticks(ys, [r["label"] for r in rows])
    ax.invert_yaxis()
    maximum = max(r["value"] for r in rows)
    ax.set_xlim(0, maximum * 1.17)
    ax.set_axisbelow(True)
    ax.xaxis.grid(True, color="#e1e6eb", linewidth=.7)
    ax.tick_params(axis="both", length=0, pad=9, colors="#263342")
    for spine in ax.spines.values():
        spine.set_visible(False)
    for y, row in zip(ys, rows):
        ax.text(row["value"] + maximum * .015, y,
                f'{row["value"]:.{data["decimals"]}f}', va="center", fontsize=12,
                weight="bold" if row["highlight"] else "normal", color="#182535")
    ax.set_xlabel(data["unit"] + "  /  lower is better", labelpad=13, color="#536273")
    fig.text(.055, .91, data["title"], fontsize=18, weight="bold", color="#182535")
    fig.text(.055, .855, data["subtitle"], fontsize=12, color="#536273")
    fig.text(.055, .145, data["annotation"], fontsize=11, linespacing=1.6, color="#245b92")
    fig.text(.055, .052, data["caption"], fontsize=10, color="#536273")
    dest = ROOT / "docs/figures"
    dest.mkdir(exist_ok=True)
    for ext in ("svg", "png"):
        metadata = {"Date": None, "Creator": "generate_portfolio_figures.py"} if ext == "svg" else {"Software": "generate_portfolio_figures.py"}
        output = dest / f"{slug}.{ext}"
        fig.savefig(output, metadata=metadata)
        if ext == "svg":
            output.write_text("\n".join(line.rstrip() for line in output.read_text(encoding="utf-8").splitlines()) + "\n", encoding="utf-8", newline="\n")
    plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Validate data against frozen JSON reports; do not render")
    args = parser.parse_args()
    for slug in SLUGS:
        data = json.loads((ROOT / "docs/data" / f"{slug}.json").read_text(encoding="utf-8"))
        check(data) if args.check else render(slug, data)
        print(("Verified " if args.check else "Generated ") + slug)
