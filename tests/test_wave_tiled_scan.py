"""Pure CPU correctness cases only. Direct entry deliberately uses no clock."""
import itertools
import unittest

from wave_tiled_scan_model import Layout, Slot, State, U32, execute, fallback_reduce, local_scan, lookback


def oracle(values):
    prefix, output = 0, []
    for value in values:
        output.append(prefix)
        prefix = (prefix + value) & U32
    return output


def pattern(count):
    extremes = [0, U32, 0x80000000, 0x40000000, 1, 0xC0000001, 7]
    return [extremes[i % len(extremes)] ^ ((i * 0x9E3779B9) & U32) for i in range(count)]


class WaveTiledScanModelTests(unittest.TestCase):
    def test_mapping_is_bijective_and_adjacent_lanes_load_adjacent_vectors(self):
        for layout in [Layout(32, 32, 4), Layout(256, 32, 16), Layout(256, 64, 12), Layout(1024, 32, 4)]:
            indices = [layout.index(thread, chunk) + component
                       for thread in range(layout.group)
                       for chunk in range(layout.items // 4) for component in range(4)]
            self.assertEqual(list(range(layout.block)), sorted(indices))
            for chunk in range(layout.items // 4):
                self.assertEqual([4] * (layout.wave - 1),
                                 [layout.index(lane + 1, chunk) - layout.index(lane, chunk)
                                  for lane in range(layout.wave - 1)])
        # The old items=16 mapping instead starts neighboring lanes 16 u32 apart.
        self.assertEqual(4, Layout().index(1, 0) - Layout().index(0, 0))

    def test_local_prefix_order_and_full_width_overflow(self):
        for layout in [Layout(32, 32, 4), Layout(), Layout(256, 64, 12), Layout(64, 64, 64)]:
            for count in sorted({min(layout.block, n) for n in
                                 [0, 1, 3, 4, 5, layout.wave * 4 - 1, layout.wave * 4 + 1,
                                  layout.block - 3, layout.block - 1, layout.block]}):
                for values in [pattern(count), [U32] * count, [0x80000000] * count, [0] * count]:
                    with self.subTest(layout=layout, count=count):
                        prefixes, total = local_scan(values, layout)
                        self.assertEqual(oracle(values), prefixes)
                        self.assertEqual(sum(values) & U32, total)
                        self.assertEqual(total, fallback_reduce(values, layout))

    def test_wave_segment_allocation_does_not_depend_on_group_index_packing(self):
        layout = Layout(128, 32, 12)
        for allocation_order in itertools.permutations(range(4)):
            logical_wave_ids = {physical_wave: ticket for ticket, physical_wave in enumerate(allocation_order)}
            # Deliberately interleave SV_GroupIndex across physical waves and
            # reverse hardware lanes; still every logical element is owned once.
            owned = []
            for group_index in range(layout.group):
                physical_wave, hardware_lane = group_index % 4, 31 - group_index // 4
                logical_thread = logical_wave_ids[physical_wave] * layout.wave + hardware_lane
                for chunk in range(layout.items // 4):
                    owned.extend(range(layout.index(logical_thread, chunk), layout.index(logical_thread, chunk) + 4))
            self.assertEqual(list(range(layout.block)), sorted(owned))

    def test_cross_partition_6145_full_width_regression(self):
        values = [U32 if i % 2 == 0 else 0x80000000 for i in range(6145)]
        output, _, _ = execute(values, Layout(), order=[1, 0])
        self.assertEqual(oracle(values), output)
        self.assertEqual(0xFFFFF400, output[6144])

    def test_complete_scan_in_forward_reverse_and_interleaved_completion_order(self):
        for layout in [Layout(32, 32, 4), Layout(), Layout(256, 64, 12)]:
            for count in [0, 1, layout.block - 1, layout.block, layout.block + 1, 4 * layout.block + 3]:
                values = pattern(count)
                blocks = (count + layout.block - 1) // layout.block
                orders = [list(range(blocks)), list(reversed(range(blocks))),
                          list(range(1, blocks, 2)) + list(range(0, blocks, 2))]
                for order, prepublish in itertools.product(orders, [False, True]):
                    output, state, trace = execute(values, layout, order=order, publish_aggregates=prepublish)
                    self.assertEqual(oracle(values), output)
                    self.assertTrue(all(slot.status == 2 for slot in state.slots))
                    if blocks > 1 and order == list(reversed(range(blocks))) and not prepublish:
                        self.assertTrue(any(event[0] == "fallback" for event in trace))

    def test_stable_compaction_with_no_all_and_mixed_selected_values(self):
        layout = Layout(64, 64, 12)
        for count in [0, 1, 3, 4, 5, layout.block - 1, layout.block, layout.block + 1, 3 * layout.block + 3]:
            for values, mask in [(pattern(count), 0), (pattern(count), 7), (pattern(count), U32),
                                 ([1] * count, 1), ([U32] * count, 0), ([0] * count, U32)]:
                blocks = (count + layout.block - 1) // layout.block
                output, _, _ = execute(values, layout, mask=mask, order=reversed(range(blocks)))
                selected = [value for value in values if value & mask == 0]
                self.assertEqual([len(selected)] + selected, output)

    def test_each_reset_invalidates_poisoned_and_previous_operation_tokens(self):
        layout = Layout(32, 32, 4)
        state = State(5)
        for count in [4 * layout.block + 1, 1, 0, 2 * layout.block - 1, 4 * layout.block + 3]:
            values = pattern(count)[::-1]
            blocks = (count + layout.block - 1) // layout.block
            state.reserved, state.next_block = 0x3FFFFFFF, U32
            output, state, _ = execute(values, layout, state=state, order=reversed(range(blocks)))
            self.assertEqual(oracle(values), output)
            self.assertEqual(0, state.reserved)
        # Reset ignores old payloads and old epoch values; it invalidates only statuses.
        old_payloads = [(s.aggregate, s.inclusive) for s in state.slots]
        state.reset(5)
        self.assertTrue(all(s.status == 0 for s in state.slots))
        self.assertEqual(old_payloads, [(s.aggregate, s.inclusive) for s in state.slots])

    def test_missing_predecessor_terminates_with_bounded_polls_without_publishing(self):
        layout = Layout(32, 32, 4)
        values = [U32] * (3 * layout.block)
        for max_polls in [1, 4, 16]:
            state = State(3)
            state.reset(3)
            before = [(s.aggregate, s.inclusive, s.status) for s in state.slots]
            prefix, trace = lookback(3, state, values, layout, max_polls)
            self.assertEqual(sum(values) & U32, prefix)
            self.assertEqual(before, [(s.aggregate, s.inclusive, s.status) for s in state.slots])
            self.assertEqual(3 * max_polls, sum(event[0] == "poll" for event in trace))
            self.assertEqual([2, 1, 0], [event[1] for event in trace if event[0] == "fallback"])

    def test_status_publication_between_observation_and_load_is_not_double_counted(self):
        layout = Layout(32, 32, 4)
        values = pattern(3 * layout.block)
        totals = [sum(values[i:i + layout.block]) & U32 for i in range(0, len(values), layout.block)]
        for observed in [0, 1, 2]:
            for transition in [False, True]:
                state = State(3)
                state.reset(3)
                state.slots = [Slot(t, sum(totals[:i + 1]) & U32, observed) for i, t in enumerate(totals)]

                def publish(index, status, current):
                    if transition:
                        current.slots[index].status = 2

                prefix, _ = lookback(3, state, values, layout, after_observe=publish)
                self.assertEqual(sum(values) & U32, prefix)

    def test_owner_publishes_inclusive_during_private_fallback(self):
        layout = Layout(32, 32, 4)
        values = pattern(3 * layout.block)
        state = State(3)
        state.reset(3)

        def publish(index, current):
            current.slots[index].aggregate = sum(values[index * layout.block:(index + 1) * layout.block]) & U32
            current.slots[index].inclusive = sum(values[:(index + 1) * layout.block]) & U32
            current.slots[index].status = 2

        prefix, trace = lookback(3, state, values, layout, during_fallback=publish)
        self.assertEqual(sum(values) & U32, prefix)
        self.assertEqual([2, 1, 0], [event[1] for event in trace if event[0] == "fallback"])

    def test_prefix_shortcut_skips_all_older_missing_partitions(self):
        layout = Layout(32, 32, 4)
        values = pattern(4 * layout.block)
        state = State(4)
        state.reset(4)
        state.slots[2] = Slot(0, sum(values[:3 * layout.block]) & U32, 2)
        state.slots[3] = Slot(sum(values[3 * layout.block:]) & U32, 0, 1)
        prefix, trace = lookback(4, state, values, layout)
        self.assertEqual(sum(values) & U32, prefix)
        self.assertEqual([3, 2], [event[1] for event in trace if event[0] == "poll"])


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(WaveTiledScanModelTests)
    count = suite.countTestCases()
    for case in suite:
        case.debug()
    print(f"PASS: {count} deterministic CPU model tests; no device or timing APIs used.")
