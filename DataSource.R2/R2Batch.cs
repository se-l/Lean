/*
 * R2Batch — the storage of one (table, day, underlying) chain-day.
 *
 * Every row lives in ONE flat array; a contract is a slice of it. The batch SQL orders by symbol
 * first, so a contract's rows are contiguous by construction, and the slices are just
 * (offset, count) pairs recorded at symbol boundaries while filling.
 *
 * Why not List<TRow> per contract: List growth doubles capacity (total allocations ~2x the
 * payload) and every array past 85 KB goes to the Large Object Heap, which is collected only in
 * gen2 and not compacted by default. For a 7.4M-row chain-day that meant several GB of dead
 * buffers to hold ~350 MB of rows. A flat array is allocated once at its exact length (from the
 * preflight count) and is a single object to cache or evict.
 */

using System;
using System.Collections.Generic;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>One chain-day of typed rows: flat storage plus per-contract slices.</summary>
    public readonly struct R2Batch<TRow>
    {
        private readonly TRow[] _rows;
        private readonly Dictionary<string, (int Offset, int Count)> _slices;

        internal R2Batch(TRow[] rows, Dictionary<string, (int Offset, int Count)> slices)
        {
            _rows = rows;
            _slices = slices;
        }

        internal TRow[] FlatArray => _rows;

        internal Dictionary<string, (int Offset, int Count)> Slices => _slices;

        /// <summary>Number of contracts carried by this chain-day (zero when the day is empty).</summary>
        public int SymbolCount => _slices?.Count ?? 0;

        /// <summary>Number of rows in the whole batch.</summary>
        public long RowCount => _rows?.LongLength ?? 0;

        /// <summary>The contracts this batch carries.</summary>
        public IEnumerable<string> Symbols => _slices?.Keys ?? (IEnumerable<string>)Array.Empty<string>();

        /// <summary>
        /// The contract's rows as a zero-copy view. A contract with no rows in this day returns an
        /// empty segment — the same answer the per-symbol path gave when the day did not carry it.
        /// </summary>
        public ArraySegment<TRow> Rows(string symbol)
        {
            return _slices != null && symbol != null && _slices.TryGetValue(symbol, out var slice)
                ? new ArraySegment<TRow>(_rows, slice.Offset, slice.Count)
                : ArraySegment<TRow>.Empty;
        }
    }
}
