"""Deterministic CPU model of scan_wave_tiled_u32.hlsli, not a GPU emulator.

No device, clock, timing, randomness, or benchmark dependencies. The sequential
oracle belongs in the tests, separately from the modeled lane/tile protocol.
"""
from dataclasses import dataclass

U32 = 0xFFFFFFFF


@dataclass(frozen=True)
class Layout:
    group: int = 256
    wave: int = 32
    items: int = 16

    def __post_init__(self):
        assert self.wave in (32, 64)
        assert self.wave <= self.group <= 1024
        assert self.group & (self.group - 1) == 0
        assert 4 <= self.items <= 64 and self.items % 4 == 0

    @property
    def block(self):
        return self.group * self.items

    def index(self, logical_thread, chunk):
        # Logical thread is the allocated wave segment plus the hardware lane,
        # not SV_GroupIndex. Physical group-index packing is irrelevant here.
        wave, lane = divmod(logical_thread, self.wave)
        return wave * self.wave * self.items + (chunk * self.wave + lane) * 4


def local_scan(values, layout):
    """Return every prefix in original element order, plus the block reduction."""
    assert len(values) <= layout.block
    padded = list(values) + [0] * (layout.block - len(values))
    prefixes = [None] * layout.block
    wave_totals = []
    for wave in range(layout.group // layout.wave):
        wave_total = 0
        for chunk in range(layout.items // 4):
            vector_prefixes, vector_sums, indices = [], [], []
            for lane in range(layout.wave):
                index = layout.index(wave * layout.wave + lane, chunk)
                x, y, z, w = padded[index:index + 4]
                indices.append(index)
                vector_prefixes.append([0, x, (x + y) & U32, (x + y + z) & U32])
                vector_sums.append((x + y + z + w) & U32)
            # WavePrefixSum, with the last inclusive lane reused as the total.
            lane_prefix = 0
            for index, vector, total in zip(indices, vector_prefixes, vector_sums):
                for component in range(4):
                    assert prefixes[index + component] is None
                    prefixes[index + component] = (vector[component] + lane_prefix + wave_total) & U32
                lane_prefix = (lane_prefix + total) & U32
            wave_total = (wave_total + lane_prefix) & U32
        wave_totals.append(wave_total)
    # Model the first-wave spine; it receives wave totals, not thread totals.
    spine = 0
    for wave, total in enumerate(wave_totals):
        start = wave * layout.wave * layout.items
        for index in range(start, start + layout.wave * layout.items):
            prefixes[index] = (prefixes[index] + spine) & U32
        spine = (spine + total) & U32
    return prefixes[:len(values)], spine


def fallback_reduce(values, layout):
    """Model the different, group-striped uint4 reduction mapping."""
    lane_totals = []
    visited = []
    for thread in range(layout.group):
        total = 0
        for chunk in range(layout.items // 4):
            index = (chunk * layout.group + thread) * 4
            visited.extend(range(index, min(index + 4, len(values))))
            total = (total + sum(values[index:index + 4])) & U32
        lane_totals.append(total)
    assert sorted(visited) == list(range(len(values)))
    wave_totals = [sum(lane_totals[i:i + layout.wave]) & U32
                   for i in range(0, layout.group, layout.wave)]
    return sum(wave_totals) & U32


@dataclass
class Slot:
    aggregate: int = 0
    inclusive: int = 0
    status: int = 0


class State:
    def __init__(self, count):
        self.reserved, self.next_block = U32, U32
        self.slots = [Slot(U32, 0x80000001, 2) for _ in range(count)]

    def reset(self, count):
        assert count <= len(self.slots)
        for slot in self.slots[:count]:
            slot.status = 0
        self.reserved = self.next_block = 0


def lookback(block, state, values, layout, max_polls=4, after_observe=None, during_fallback=None):
    """Hooks represent owner publication between atomic observation and payload
    consumption. Event counts are simulated protocol steps, never elapsed time.
    Fallback must not modify any predecessor state.
    """
    assert 1 <= max_polls <= 16
    predecessor, suffix, misses = block - 1, 0, 0
    trace = []
    while predecessor >= 0:
        slot = state.slots[predecessor]
        status = slot.status
        trace.append(("poll", predecessor, status))
        if after_observe:
            after_observe(predecessor, status, state)
        if status == 2:
            suffix = (suffix + slot.inclusive) & U32
            trace.append(("prefix", predecessor))
            break
        if status == 1:
            suffix = (suffix + slot.aggregate) & U32
            trace.append(("aggregate", predecessor))
            predecessor -= 1
            misses = 0
        else:
            misses += 1
            if misses == max_polls:
                if during_fallback:
                    during_fallback(predecessor, state)
                first = predecessor * layout.block
                total = fallback_reduce(values[first:first + layout.block], layout)
                suffix = (suffix + total) & U32
                trace.append(("fallback", predecessor))
                predecessor -= 1
                misses = 0
    return suffix, trace


def execute(values, layout, *, mask=None, state=None, order=None, publish_aggregates=False):
    """One complete operation with a deterministic order of owner completion.
    Reverse order models assigned but suspended predecessors, without waiting.
    """
    values = list(values)
    scanned = values if mask is None else [int((value & mask) == 0) for value in values]
    count = (len(values) + layout.block - 1) // layout.block
    state = state or State(count)
    state.reset(count)
    order = list(range(count)) if order is None else list(order)
    assert sorted(order) == list(range(count))
    locals_and_totals = [local_scan(scanned[i:i + layout.block], layout)
                        for i in range(0, len(values), layout.block)]
    if publish_aggregates:
        for slot, (_, total) in zip(state.slots, locals_and_totals):
            slot.aggregate, slot.status = total, 1
    selected = sum(scanned) if mask is not None else len(values)
    output = [None] * (selected + (mask is not None))
    if mask is not None and count == 0:
        output[0] = 0
    trace = []
    for block in order:
        prefixes, total = locals_and_totals[block]
        slot = state.slots[block]
        slot.aggregate, slot.status = total, 1
        prefix, events = lookback(block, state, scanned, layout)
        trace.extend(events)
        slot.inclusive, slot.status = (prefix + total) & U32, 2
        if mask is not None and block + 1 == count:
            output[0] = slot.inclusive
        for offset, local in enumerate(prefixes):
            index = block * layout.block + offset
            position = (local + prefix) & U32
            if mask is None:
                assert output[index] is None
                output[index] = position
            elif scanned[index]:
                assert output[position + 1] is None
                output[position + 1] = values[index]
    assert None not in output
    return output, state, trace
